using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OficinaExecucao.Api.Infra;
using OficinaExecucao.Domain;
using OficinaMecanica.Contracts;

namespace OficinaExecucao.Api.Controllers;

public record ExecucaoResponse(
    Guid OrdemServicoId,
    string NumeroOs,
    StatusExecucao Status,
    int ServicosConcluidos,
    int TotalServicos,
    IReadOnlyList<ServicoItem> Servicos,
    IReadOnlyList<RegistroHistorico> Historico,
    DateTime CriadaEm,
    DateTime AtualizadaEm,
    string? MotivoFalha)
{
    public static ExecucaoResponse De(Execucao e) => new(e.Id, e.NumeroOs, e.Status, e.ServicosConcluidos,
        e.Servicos.Count, e.Servicos, e.Historico, e.CriadaEm, e.AtualizadaEm, e.MotivoFalha);
}

public record FinalizarServicoResponse(bool ExecucaoFinalizada, ExecucaoResponse Execucao);

public record RegistrarFalhaRequest(string Motivo);

/// <summary>Fila de execucao e acompanhamento da producao (mecanicos).</summary>
[ApiController]
[Route("api/execucoes")]
[Authorize(Roles = "Administrador,Mecanico,Recepcionista")]
public class ExecucoesController(
    IExecucaoRepository repositorio,
    IPublishEndpoint publisher,
    ExecucaoMetrics metrics,
    ILogger<ExecucoesController> logger) : ControllerBase
{
    private const string PodeExecutar = "Administrador,Mecanico";

    /// <summary>Fila de execucao: EmReparo, depois EmDiagnostico, depois NaFila (mais antigas primeiro).</summary>
    [HttpGet]
    public async Task<ActionResult<IEnumerable<ExecucaoResponse>>> Fila(CancellationToken ct) =>
        Ok(Execucao.OrdenarFila(await repositorio.ListarEmAndamentoAsync(ct)).Select(ExecucaoResponse.De));

    /// <summary>Detalhes da execucao, com progresso dos servicos e historico.</summary>
    [HttpGet("{ordemServicoId:guid}")]
    public async Task<ActionResult<ExecucaoResponse>> Obter(Guid ordemServicoId, CancellationToken ct)
    {
        var execucao = await repositorio.ObterAsync(ordemServicoId, ct);
        return execucao is null ? NotFound() : ExecucaoResponse.De(execucao);
    }

    /// <summary>NaFila -> EmDiagnostico.</summary>
    [HttpPost("{ordemServicoId:guid}/iniciar-diagnostico")]
    [Authorize(Roles = PodeExecutar)]
    public async Task<IActionResult> IniciarDiagnostico(Guid ordemServicoId, CancellationToken ct)
    {
        var (execucao, _, erro) = await Aplicar(ordemServicoId, e => { e.IniciarDiagnostico(); return false; }, ct);
        return erro ?? Ok(ExecucaoResponse.De(execucao!));
    }

    /// <summary>EmDiagnostico -> EmReparo (finaliza direto se a OS nao tiver servicos a executar).</summary>
    [HttpPost("{ordemServicoId:guid}/iniciar-reparo")]
    [Authorize(Roles = PodeExecutar)]
    public async Task<IActionResult> IniciarReparo(Guid ordemServicoId, CancellationToken ct)
    {
        var (execucao, _, erro) = await Aplicar(ordemServicoId, e => e.IniciarReparo(), ct);
        return erro ?? Ok(ExecucaoResponse.De(execucao!));
    }

    /// <summary>Inicia a execucao de um servico da OS (exige EmReparo).</summary>
    [HttpPut("{ordemServicoId:guid}/servicos/{servicoId:guid}/iniciar")]
    [Authorize(Roles = PodeExecutar)]
    public async Task<IActionResult> IniciarServico(Guid ordemServicoId, Guid servicoId, CancellationToken ct)
    {
        var (execucao, _, erro) = await Aplicar(ordemServicoId, e => { e.IniciarServico(servicoId); return false; }, ct);
        return erro ?? Ok(ExecucaoResponse.De(execucao!));
    }

    /// <summary>Finaliza um servico. Se for o ultimo, a execucao e finalizada e ExecucaoFinalizada e publicado.</summary>
    [HttpPut("{ordemServicoId:guid}/servicos/{servicoId:guid}/finalizar")]
    [Authorize(Roles = PodeExecutar)]
    public async Task<IActionResult> FinalizarServico(Guid ordemServicoId, Guid servicoId, CancellationToken ct)
    {
        var (execucao, finalizada, erro) = await Aplicar(ordemServicoId, e => e.FinalizarServico(servicoId), ct);
        return erro ?? Ok(new FinalizarServicoResponse(finalizada, ExecucaoResponse.De(execucao!)));
    }

    /// <summary>Registra falha na execucao e publica ExecucaoFalhou (a saga dispara o estorno do pagamento).</summary>
    [HttpPost("{ordemServicoId:guid}/falha")]
    [Authorize(Roles = PodeExecutar)]
    public async Task<IActionResult> RegistrarFalha(Guid ordemServicoId, RegistrarFalhaRequest request, CancellationToken ct)
    {
        var (execucao, _, erro) = await Aplicar(ordemServicoId, e => { e.RegistrarFalha(request.Motivo); return false; }, ct);
        if (erro is not null)
            return erro;

        await publisher.Publish(new ExecucaoFalhou(execucao!.Id, execucao.MotivoFalha!), ct);
        metrics.RegistrarFalha();
        logger.LogWarning("Execucao da OS {OrdemServicoId} falhou: {Motivo}", execucao.Id, execucao.MotivoFalha);
        return Ok(ExecucaoResponse.De(execucao));
    }

    /// <summary>
    /// Carrega, aplica a transicao (retorno true = execucao finalizada), salva e publica ExecucaoFinalizada.
    /// Erros de regra viram 400; OS/servico inexistente vira 404.
    /// </summary>
    // ponytail: salva e depois publica, sem outbox transacional. Se o publish falhar apos o save o evento se perde;
    // trocar pelo outbox do MassTransit (MassTransit.MongoDb) se isso acontecer na pratica.
    private async Task<(Execucao? Execucao, bool Finalizada, IActionResult? Erro)> Aplicar(
        Guid id, Func<Execucao, bool> transicao, CancellationToken ct)
    {
        var execucao = await repositorio.ObterAsync(id, ct);
        if (execucao is null)
            return (null, false, NotFound(new { erro = $"Execucao da OS {id} nao encontrada." }));

        bool finalizada;
        try
        {
            finalizada = transicao(execucao);
        }
        catch (KeyNotFoundException ex)
        {
            return (null, false, NotFound(new { erro = ex.Message }));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return (null, false, BadRequest(new { erro = ex.Message }));
        }

        await repositorio.SalvarAsync(execucao, ct);

        if (finalizada)
        {
            await publisher.Publish(new ExecucaoFinalizada(execucao.Id, execucao.AtualizadaEm), ct);
            metrics.RegistrarFinalizacao(execucao.AtualizadaEm - execucao.CriadaEm);
            logger.LogInformation("Execucao da OS {OrdemServicoId} finalizada", execucao.Id);
        }

        return (execucao, finalizada, null);
    }
}

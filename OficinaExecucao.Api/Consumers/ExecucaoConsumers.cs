using MassTransit;
using OficinaExecucao.Api.Infra;
using OficinaExecucao.Domain;
using OficinaMecanica.Contracts;

namespace OficinaExecucao.Api.Consumers;

/// <summary>
/// Comando da saga (OS Service): coloca a OS na fila de execucao. Idempotente por OrdemServicoId:
/// reentregas nao recriam o documento, apenas republicam ExecucaoIniciada.
/// </summary>
public class IniciarExecucaoConsumer(IExecucaoRepository repositorio, ILogger<IniciarExecucaoConsumer> logger)
    : IConsumer<IniciarExecucao>
{
    public async Task Consume(ConsumeContext<IniciarExecucao> context)
    {
        var msg = context.Message;

        if (await repositorio.ObterAsync(msg.OrdemServicoId, context.CancellationToken) is null)
        {
            var execucao = new Execucao(msg.OrdemServicoId, msg.NumeroOs,
                msg.Servicos.Select(s => new ServicoItem(s.ServicoId, s.Descricao, s.Quantidade)));
            await repositorio.SalvarAsync(execucao, context.CancellationToken);
            logger.LogInformation("OS {OrdemServicoId} ({NumeroOs}) entrou na fila de execucao", msg.OrdemServicoId, msg.NumeroOs);
        }
        else
        {
            logger.LogInformation("IniciarExecucao duplicado para OS {OrdemServicoId} - ignorado", msg.OrdemServicoId);
        }

        await context.Publish(new ExecucaoIniciada(msg.OrdemServicoId), context.CancellationToken);
    }
}

/// <summary>Compensacao da saga: cancela a execucao se ainda nao foi finalizada. Idempotente.</summary>
public class CancelarExecucaoConsumer(IExecucaoRepository repositorio, ILogger<CancelarExecucaoConsumer> logger)
    : IConsumer<CancelarExecucao>
{
    public async Task Consume(ConsumeContext<CancelarExecucao> context)
    {
        var msg = context.Message;
        var execucao = await repositorio.ObterAsync(msg.OrdemServicoId, context.CancellationToken);

        if (execucao is null || execucao.Status is StatusExecucao.Cancelada or StatusExecucao.Finalizada)
        {
            logger.LogInformation("CancelarExecucao ignorado para OS {OrdemServicoId} (status: {Status})",
                msg.OrdemServicoId, execucao?.Status.ToString() ?? "inexistente");
            return;
        }

        execucao.Cancelar(msg.Motivo);
        await repositorio.SalvarAsync(execucao, context.CancellationToken);
        logger.LogInformation("Execucao da OS {OrdemServicoId} cancelada: {Motivo}", msg.OrdemServicoId, msg.Motivo);
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MassTransit.Testing;
using OficinaExecucao.Api.Controllers;
using OficinaExecucao.Api.Infra;
using OficinaExecucao.Domain;
using OficinaMecanica.Contracts;

namespace OficinaExecucao.Tests;

public class ApiTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private readonly HttpClient _mecanico = factory.Cliente("Mecanico");

    private async Task<Execucao> Salvar(Execucao e)
    {
        await factory.Repositorio.SalvarAsync(e);
        return e;
    }

    [Fact]
    public async Task Sem_token_retorna_401()
    {
        var resp = await factory.Cliente(null).GetAsync("/api/execucoes");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Cliente_nao_acessa_a_fila()
    {
        var resp = await factory.Cliente("Cliente").GetAsync("/api/execucoes");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Recepcionista_le_mas_nao_executa()
    {
        var e = await Salvar(Dados.NovaExecucao());
        var recepcao = factory.Cliente("Recepcionista");

        Assert.Equal(HttpStatusCode.OK, (await recepcao.GetAsync($"/api/execucoes/{e.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await recepcao.PostAsync($"/api/execucoes/{e.Id}/iniciar-diagnostico", null)).StatusCode);
    }

    [Fact]
    public async Task Fila_vem_ordenada_por_prioridade()
    {
        var naFila = await Salvar(Dados.NovaExecucao());
        var reparo = await Salvar(Dados.EmReparo());
        var diag = Dados.NovaExecucao();
        diag.IniciarDiagnostico();
        await Salvar(diag);

        var fila = await _mecanico.GetFromJsonAsync<List<ExecucaoResponse>>("/api/execucoes", Json);

        var ids = fila!.Select(f => f.OrdemServicoId).ToList();
        Assert.True(ids.IndexOf(reparo.Id) < ids.IndexOf(diag.Id));
        Assert.True(ids.IndexOf(diag.Id) < ids.IndexOf(naFila.Id));
    }

    [Fact]
    public async Task Detalhe_retorna_progresso_e_historico_ou_404()
    {
        var e = Dados.EmReparo(2);
        e.IniciarServico(e.Servicos[0].ServicoId);
        e.FinalizarServico(e.Servicos[0].ServicoId);
        await Salvar(e);

        var detalhe = await _mecanico.GetFromJsonAsync<ExecucaoResponse>($"/api/execucoes/{e.Id}", Json);

        Assert.Equal(1, detalhe!.ServicosConcluidos);
        Assert.Equal(2, detalhe.TotalServicos);
        Assert.Equal(StatusExecucao.EmReparo, detalhe.Status);
        Assert.NotEmpty(detalhe.Historico);
        Assert.Equal(HttpStatusCode.NotFound, (await _mecanico.GetAsync($"/api/execucoes/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Fluxo_pela_api_finaliza_e_publica_ExecucaoFinalizada()
    {
        var e = await Salvar(Dados.NovaExecucao(1));
        var servico = e.Servicos[0].ServicoId;

        Assert.Equal(HttpStatusCode.OK, (await _mecanico.PostAsync($"/api/execucoes/{e.Id}/iniciar-diagnostico", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _mecanico.PostAsync($"/api/execucoes/{e.Id}/iniciar-reparo", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _mecanico.PutAsync($"/api/execucoes/{e.Id}/servicos/{servico}/iniciar", null)).StatusCode);

        var resp = await _mecanico.PutAsync($"/api/execucoes/{e.Id}/servicos/{servico}/finalizar", null);
        var corpo = await resp.Content.ReadFromJsonAsync<FinalizarServicoResponse>(Json);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(corpo!.ExecucaoFinalizada);
        Assert.Equal(StatusExecucao.Finalizada, corpo.Execucao.Status);
        Assert.True(await factory.Harness.Published.Any<ExecucaoFinalizada>(x => x.Context.Message.OrdemServicoId == e.Id));
    }

    [Fact]
    public async Task Finalizar_servico_que_nao_e_o_ultimo_nao_publica()
    {
        var e = Dados.EmReparo(2);
        e.IniciarServico(e.Servicos[0].ServicoId);
        await Salvar(e);

        var resp = await _mecanico.PutAsync($"/api/execucoes/{e.Id}/servicos/{e.Servicos[0].ServicoId}/finalizar", null);
        var corpo = await resp.Content.ReadFromJsonAsync<FinalizarServicoResponse>(Json);

        Assert.False(corpo!.ExecucaoFinalizada);
        Assert.False(await factory.Harness.Published.Any<ExecucaoFinalizada>(x => x.Context.Message.OrdemServicoId == e.Id));
    }

    [Fact]
    public async Task Falha_publica_ExecucaoFalhou()
    {
        var e = await Salvar(Dados.EmReparo());

        var resp = await _mecanico.PostAsJsonAsync($"/api/execucoes/{e.Id}/falha", new RegistrarFalhaRequest("Motor travado"));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.True(await factory.Harness.Published.Any<ExecucaoFalhou>(
            x => x.Context.Message.OrdemServicoId == e.Id && x.Context.Message.Motivo == "Motor travado"));
        Assert.Equal(StatusExecucao.Falhou, e.Status);
    }

    [Fact]
    public async Task Transicao_invalida_retorna_400()
    {
        var e = await Salvar(Dados.NovaExecucao());

        Assert.Equal(HttpStatusCode.BadRequest, (await _mecanico.PostAsync($"/api/execucoes/{e.Id}/iniciar-reparo", null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _mecanico.PostAsJsonAsync($"/api/execucoes/{e.Id}/falha", new RegistrarFalhaRequest(""))).StatusCode);
        Assert.False(await factory.Harness.Published.Any<ExecucaoFalhou>(x => x.Context.Message.OrdemServicoId == e.Id));
    }

    [Fact]
    public async Task Os_ou_servico_inexistente_retorna_404()
    {
        var e = await Salvar(Dados.EmReparo());

        Assert.Equal(HttpStatusCode.NotFound,
            (await _mecanico.PostAsync($"/api/execucoes/{Guid.NewGuid()}/iniciar-diagnostico", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _mecanico.PutAsync($"/api/execucoes/{e.Id}/servicos/{Guid.NewGuid()}/iniciar", null)).StatusCode);
    }

    [Fact]
    public async Task Health_swagger_e_correlation_id()
    {
        var client = factory.Cliente(null);
        client.DefaultRequestHeaders.Add(CorrelationIdMiddleware.HeaderName, "abc-123");

        var health = await client.GetAsync("/health");
        var swagger = await client.GetAsync("/execucao/swagger/v1/swagger.json");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("abc-123", health.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());
        Assert.Equal(HttpStatusCode.OK, swagger.StatusCode);
        Assert.Contains("/api/execucoes/{ordemServicoId}/falha", await swagger.Content.ReadAsStringAsync());
    }
}

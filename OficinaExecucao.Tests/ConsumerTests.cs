using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using OficinaExecucao.Api.Consumers;
using OficinaExecucao.Api.Infra;
using OficinaExecucao.Domain;
using OficinaMecanica.Contracts;

namespace OficinaExecucao.Tests;

public class ConsumerTests : IAsyncLifetime
{
    private readonly InMemoryExecucaoRepository _repositorio = new();
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;

    public async Task InitializeAsync()
    {
        _provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IExecucaoRepository>(_repositorio)
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<IniciarExecucaoConsumer>();
                x.AddConsumer<CancelarExecucaoConsumer>();
            })
            .BuildServiceProvider(true);
        _harness = _provider.GetTestHarness();
        await _harness.Start();
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    private static IniciarExecucao Comando(Guid id) => new(id, "OS-2026-0042",
        new[] { new ServicoExecucao(Guid.NewGuid(), "Troca de oleo", 1), new ServicoExecucao(Guid.NewGuid(), "Alinhamento", 1) });

    [Fact]
    public async Task IniciarExecucao_cria_na_fila_e_publica_ExecucaoIniciada()
    {
        var id = Guid.NewGuid();

        await _harness.Bus.Publish(Comando(id));

        Assert.True(await _harness.GetConsumerHarness<IniciarExecucaoConsumer>().Consumed.Any<IniciarExecucao>());
        Assert.True(await _harness.Published.Any<ExecucaoIniciada>(x => x.Context.Message.OrdemServicoId == id));
        var execucao = await _repositorio.ObterAsync(id);
        Assert.NotNull(execucao);
        Assert.Equal(StatusExecucao.NaFila, execucao!.Status);
        Assert.Equal("OS-2026-0042", execucao.NumeroOs);
        Assert.Equal(2, execucao.Servicos.Count);
    }

    [Fact]
    public async Task IniciarExecucao_duplicado_nao_recria_documento()
    {
        var id = Guid.NewGuid();
        await _harness.Bus.Publish(Comando(id));
        Assert.True(await _harness.Published.Any<ExecucaoIniciada>(x => x.Context.Message.OrdemServicoId == id));

        var execucao = (await _repositorio.ObterAsync(id))!;
        execucao.IniciarDiagnostico();
        await _repositorio.SalvarAsync(execucao);

        await _harness.Bus.Publish(Comando(id));
        await _harness.InactivityTask;

        Assert.Equal(2, _harness.Published.Select<ExecucaoIniciada>(x => x.Context.Message.OrdemServicoId == id).Count());
        Assert.Equal(StatusExecucao.EmDiagnostico, (await _repositorio.ObterAsync(id))!.Status);
    }

    [Fact]
    public async Task CancelarExecucao_cancela_execucao_em_andamento()
    {
        var execucao = Dados.NovaExecucao();
        await _repositorio.SalvarAsync(execucao);

        await _harness.Bus.Publish(new CancelarExecucao(execucao.Id, "Pagamento estornado"));

        Assert.True(await _harness.GetConsumerHarness<CancelarExecucaoConsumer>().Consumed.Any<CancelarExecucao>(
            x => x.Context.Message.OrdemServicoId == execucao.Id));
        Assert.Equal(StatusExecucao.Cancelada, (await _repositorio.ObterAsync(execucao.Id))!.Status);
    }

    [Fact]
    public async Task CancelarExecucao_e_idempotente_e_nao_mexe_em_finalizada_nem_inexistente()
    {
        var cancelada = Dados.NovaExecucao();
        cancelada.Cancelar("primeira vez");
        var finalizada = Dados.EmReparo(1);
        finalizada.IniciarServico(finalizada.Servicos[0].ServicoId);
        finalizada.FinalizarServico(finalizada.Servicos[0].ServicoId);
        await _repositorio.SalvarAsync(cancelada);
        await _repositorio.SalvarAsync(finalizada);

        await _harness.Bus.Publish(new CancelarExecucao(cancelada.Id, "de novo"));
        await _harness.Bus.Publish(new CancelarExecucao(finalizada.Id, "tarde demais"));
        await _harness.Bus.Publish(new CancelarExecucao(Guid.NewGuid(), "nunca existiu"));
        await _harness.InactivityTask;

        Assert.Equal(3, _harness.GetConsumerHarness<CancelarExecucaoConsumer>().Consumed.Select<CancelarExecucao>().Count());
        Assert.False(await _harness.Consumed.Any<CancelarExecucao>(x => x.Exception is not null));
        Assert.Equal(2, cancelada.Historico.Count); // nao duplicou o registro de cancelamento
        Assert.Equal(StatusExecucao.Finalizada, finalizada.Status);
    }
}

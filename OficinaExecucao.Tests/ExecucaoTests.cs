using OficinaExecucao.Domain;

namespace OficinaExecucao.Tests;

public class ExecucaoTests
{
    [Fact]
    public void Nova_execucao_entra_na_fila_com_historico()
    {
        var e = Dados.NovaExecucao();

        Assert.Equal(StatusExecucao.NaFila, e.Status);
        Assert.Single(e.Historico);
        Assert.Equal(StatusExecucao.NaFila, e.Historico[0].Status);
        Assert.Equal(0, e.ServicosConcluidos);
    }

    [Fact]
    public void Construtor_exige_ordem_servico_id()
    {
        Assert.Throws<ArgumentException>(() => new Execucao(Guid.Empty, "OS", Array.Empty<ServicoItem>()));
    }

    [Fact]
    public void Fluxo_completo_finaliza_no_ultimo_servico()
    {
        var e = Dados.NovaExecucao(2);
        e.IniciarDiagnostico();
        Assert.Equal(StatusExecucao.EmDiagnostico, e.Status);
        Assert.False(e.IniciarReparo());
        Assert.Equal(StatusExecucao.EmReparo, e.Status);

        var (s1, s2) = (e.Servicos[0].ServicoId, e.Servicos[1].ServicoId);
        e.IniciarServico(s1);
        e.IniciarServico(s2);
        Assert.False(e.FinalizarServico(s1));
        Assert.Equal(StatusExecucao.EmReparo, e.Status);
        Assert.Equal(1, e.ServicosConcluidos);

        Assert.True(e.FinalizarServico(s2));
        Assert.Equal(StatusExecucao.Finalizada, e.Status);
        Assert.Equal(StatusExecucao.Finalizada, e.Historico[^1].Status);
        Assert.Equal(8, e.Historico.Count); // fila, diag, reparo, 2 inicios, 2 fins, finalizada
    }

    [Fact]
    public void Reparo_sem_servicos_finaliza_direto()
    {
        var e = Dados.NovaExecucao(0);
        e.IniciarDiagnostico();

        Assert.True(e.IniciarReparo());
        Assert.Equal(StatusExecucao.Finalizada, e.Status);
    }

    [Fact]
    public void Transicoes_fora_de_ordem_sao_rejeitadas()
    {
        var e = Dados.NovaExecucao();
        var servico = e.Servicos[0].ServicoId;

        Assert.Throws<InvalidOperationException>(() => e.IniciarReparo());
        Assert.Throws<InvalidOperationException>(() => e.IniciarServico(servico));
        Assert.Throws<InvalidOperationException>(() => e.FinalizarServico(servico));

        e.IniciarDiagnostico();
        Assert.Throws<InvalidOperationException>(() => e.IniciarDiagnostico());
        Assert.Throws<InvalidOperationException>(() => e.IniciarServico(servico));
    }

    [Fact]
    public void Servico_precisa_ser_iniciado_uma_vez_antes_de_finalizar_uma_vez()
    {
        var e = Dados.EmReparo();
        var servico = e.Servicos[0].ServicoId;

        Assert.Throws<InvalidOperationException>(() => e.FinalizarServico(servico));
        e.IniciarServico(servico);
        Assert.Throws<InvalidOperationException>(() => e.IniciarServico(servico));
        e.FinalizarServico(servico);
        Assert.Throws<InvalidOperationException>(() => e.FinalizarServico(servico));
    }

    [Fact]
    public void Servico_inexistente_lanca_KeyNotFound()
    {
        var e = Dados.EmReparo();
        Assert.Throws<KeyNotFoundException>(() => e.IniciarServico(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Falha_pode_ser_registrada_em_andamento(int passos)
    {
        var e = Dados.NovaExecucao();
        if (passos >= 1) e.IniciarDiagnostico();
        if (passos >= 2) e.IniciarReparo();

        e.RegistrarFalha("Peca com defeito");

        Assert.Equal(StatusExecucao.Falhou, e.Status);
        Assert.Equal("Peca com defeito", e.MotivoFalha);
        Assert.Equal("Peca com defeito", e.Historico[^1].Observacao);
    }

    [Fact]
    public void Falha_exige_motivo_e_status_em_andamento()
    {
        var e = Dados.NovaExecucao();
        Assert.Throws<ArgumentException>(() => e.RegistrarFalha(" "));

        e.Cancelar("cliente desistiu");
        Assert.Throws<InvalidOperationException>(() => e.RegistrarFalha("x"));
    }

    [Fact]
    public void Cancelar_nao_permite_execucao_finalizada()
    {
        var e = Dados.EmReparo(1);
        var servico = e.Servicos[0].ServicoId;
        e.IniciarServico(servico);
        e.FinalizarServico(servico);

        Assert.Throws<InvalidOperationException>(() => e.Cancelar("tarde demais"));
    }

    [Fact]
    public void Cancelar_a_partir_de_falha_e_permitido()
    {
        var e = Dados.NovaExecucao();
        e.RegistrarFalha("x");
        e.Cancelar("estorno concluido");
        Assert.Equal(StatusExecucao.Cancelada, e.Status);
    }

    [Fact]
    public void Fila_ordena_por_status_e_antiguidade_e_ignora_finais()
    {
        var naFilaAntiga = Dados.NovaExecucao();
        Thread.Sleep(5);
        var naFilaNova = Dados.NovaExecucao();
        var reparo = Dados.EmReparo();
        var diagnostico = Dados.NovaExecucao();
        diagnostico.IniciarDiagnostico();
        var cancelada = Dados.NovaExecucao();
        cancelada.Cancelar("x");

        var fila = Execucao.OrdenarFila(new[] { naFilaNova, cancelada, diagnostico, naFilaAntiga, reparo }).ToList();

        Assert.Equal(new[] { reparo, diagnostico, naFilaAntiga, naFilaNova }, fila);
    }
}

namespace OficinaExecucao.Domain;

public enum StatusExecucao
{
    NaFila,
    EmDiagnostico,
    EmReparo,
    Finalizada,
    Falhou,
    Cancelada
}

public record RegistroHistorico(StatusExecucao Status, DateTime Em, string? Observacao);

/// <summary>Servico da OS a ser executado pelo mecanico (snapshot vindo do OS Service).</summary>
public class ServicoItem
{
    public Guid ServicoId { get; private set; }
    public string Descricao { get; private set; } = string.Empty;
    public int Quantidade { get; private set; }
    public DateTime? IniciadoEm { get; private set; }
    public DateTime? FinalizadoEm { get; private set; }

    private ServicoItem() { }

    public ServicoItem(Guid servicoId, string descricao, int quantidade)
    {
        ServicoId = servicoId;
        Descricao = descricao;
        Quantidade = quantidade;
    }

    internal void Iniciar()
    {
        if (IniciadoEm.HasValue)
            throw new InvalidOperationException("Este servico ja foi iniciado.");
        IniciadoEm = DateTime.UtcNow;
    }

    internal void Finalizar()
    {
        if (!IniciadoEm.HasValue)
            throw new InvalidOperationException("O servico precisa ser iniciado antes de ser finalizado.");
        if (FinalizadoEm.HasValue)
            throw new InvalidOperationException("Este servico ja foi finalizado.");
        FinalizadoEm = DateTime.UtcNow;
    }
}

/// <summary>
/// Agregado da execucao de uma OS. Id = OrdemServicoId (correlacao da saga).
/// Documento auto-contido: servicos e historico ficam aninhados (MongoDB).
/// </summary>
public class Execucao
{
    public static readonly StatusExecucao[] StatusEmAndamento =
        { StatusExecucao.EmReparo, StatusExecucao.EmDiagnostico, StatusExecucao.NaFila };

    public Guid Id { get; private set; }
    public string NumeroOs { get; private set; } = string.Empty;
    public StatusExecucao Status { get; private set; }
    public List<ServicoItem> Servicos { get; private set; } = new();
    public List<RegistroHistorico> Historico { get; private set; } = new();
    public DateTime CriadaEm { get; private set; }
    public DateTime AtualizadaEm { get; private set; }
    public string? MotivoFalha { get; private set; }

    public int ServicosConcluidos => Servicos.Count(s => s.FinalizadoEm.HasValue);

    private Execucao() { }

    public Execucao(Guid ordemServicoId, string numeroOs, IEnumerable<ServicoItem> servicos)
    {
        if (ordemServicoId == Guid.Empty)
            throw new ArgumentException("OrdemServicoId e obrigatorio.", nameof(ordemServicoId));

        Id = ordemServicoId;
        NumeroOs = numeroOs;
        Servicos = servicos.ToList();
        CriadaEm = DateTime.UtcNow;
        MudarStatus(StatusExecucao.NaFila, "OS entrou na fila de execucao");
    }

    /// <summary>Fila: EmReparo > EmDiagnostico > NaFila, mais antigas primeiro dentro do status.</summary>
    public static IEnumerable<Execucao> OrdenarFila(IEnumerable<Execucao> execucoes) =>
        execucoes
            .Where(e => StatusEmAndamento.Contains(e.Status))
            .OrderBy(e => Array.IndexOf(StatusEmAndamento, e.Status))
            .ThenBy(e => e.CriadaEm);

    public void IniciarDiagnostico()
    {
        ExigirStatus(StatusExecucao.NaFila);
        MudarStatus(StatusExecucao.EmDiagnostico, null);
    }

    /// <returns>true se a execucao foi finalizada (OS sem servicos a executar).</returns>
    public bool IniciarReparo()
    {
        ExigirStatus(StatusExecucao.EmDiagnostico);
        MudarStatus(StatusExecucao.EmReparo, null);

        if (Servicos.Count > 0)
            return false;

        MudarStatus(StatusExecucao.Finalizada, "OS sem servicos a executar");
        return true;
    }

    public void IniciarServico(Guid servicoId)
    {
        ExigirStatus(StatusExecucao.EmReparo);
        var item = ObterServico(servicoId);
        item.Iniciar();
        Tocar($"Servico '{item.Descricao}' iniciado");
    }

    /// <returns>true se era o ultimo servico e a execucao foi finalizada.</returns>
    public bool FinalizarServico(Guid servicoId)
    {
        ExigirStatus(StatusExecucao.EmReparo);
        var item = ObterServico(servicoId);
        item.Finalizar();
        Tocar($"Servico '{item.Descricao}' finalizado");

        if (ServicosConcluidos < Servicos.Count)
            return false;

        MudarStatus(StatusExecucao.Finalizada, "Todos os servicos foram concluidos");
        return true;
    }

    public void RegistrarFalha(string motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ArgumentException("Motivo da falha e obrigatorio.", nameof(motivo));
        ExigirStatus(StatusEmAndamento);
        MotivoFalha = motivo;
        MudarStatus(StatusExecucao.Falhou, motivo);
    }

    public void Cancelar(string motivo)
    {
        if (Status == StatusExecucao.Finalizada)
            throw new InvalidOperationException("Execucao ja finalizada nao pode ser cancelada.");
        MudarStatus(StatusExecucao.Cancelada, motivo);
    }

    private ServicoItem ObterServico(Guid servicoId) =>
        Servicos.FirstOrDefault(s => s.ServicoId == servicoId)
        ?? throw new KeyNotFoundException($"Servico {servicoId} nao encontrado nesta execucao.");

    private void ExigirStatus(params StatusExecucao[] permitidos)
    {
        if (!permitidos.Contains(Status))
            throw new InvalidOperationException(
                $"Transicao invalida: execucao esta '{Status}' (esperado: {string.Join(", ", permitidos)}).");
    }

    private void MudarStatus(StatusExecucao novo, string? observacao)
    {
        Status = novo;
        Tocar(observacao);
    }

    private void Tocar(string? observacao)
    {
        AtualizadaEm = DateTime.UtcNow;
        Historico.Add(new RegistroHistorico(Status, AtualizadaEm, observacao));
    }
}

using System.Diagnostics.Metrics;

namespace OficinaExecucao.Api.Infra;

/// <summary>Metricas de negocio exportadas via OpenTelemetry (New Relic).</summary>
public class ExecucaoMetrics
{
    public const string MeterName = "OficinaExecucao";

    private readonly Histogram<double> _duracao;
    private readonly Counter<long> _falhas;

    public ExecucaoMetrics(IMeterFactory meterFactory)
    {
        var meter = meterFactory.Create(MeterName);
        _duracao = meter.CreateHistogram<double>("execucao.duracao_segundos", "s",
            "Tempo da entrada na fila ate a finalizacao da execucao");
        _falhas = meter.CreateCounter<long>("execucao.falhas", description: "Execucoes que falharam");
    }

    public void RegistrarFinalizacao(TimeSpan duracao) => _duracao.Record(duracao.TotalSeconds);

    public void RegistrarFalha() => _falhas.Add(1);
}

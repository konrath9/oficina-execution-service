using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using OficinaExecucao.Api.Infra;
using OficinaExecucao.Domain;

namespace OficinaExecucao.Tests;

/// <summary>Garante que o agregado (setters privados, listas aninhadas) faz round-trip em BSON sem servidor Mongo.</summary>
public class MongoMapeamentoTests
{
    [Fact]
    public void Execucao_faz_round_trip_em_bson()
    {
        MongoMapeamento.Registrar();
        MongoMapeamento.Registrar(); // idempotente

        var original = Dados.EmReparo();
        original.IniciarServico(original.Servicos[0].ServicoId);

        var doc = original.ToBsonDocument();
        var copia = BsonSerializer.Deserialize<Execucao>(doc);

        Assert.Equal(original.Id, doc["_id"].AsGuid);
        Assert.Equal("EmReparo", doc["Status"].AsString);
        Assert.Equal(original.Id, copia.Id);
        Assert.Equal(original.NumeroOs, copia.NumeroOs);
        Assert.Equal(original.Status, copia.Status);
        Assert.Equal(original.Servicos.Count, copia.Servicos.Count);
        Assert.Equal(original.Servicos[0].ServicoId, copia.Servicos[0].ServicoId);
        Assert.NotNull(copia.Servicos[0].IniciadoEm);
        Assert.Equal(original.Historico.Count, copia.Historico.Count);
        Assert.Equal(original.Historico[^1].Observacao, copia.Historico[^1].Observacao);
    }
}

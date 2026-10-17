using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Conventions;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using OficinaExecucao.Domain;

namespace OficinaExecucao.Api.Infra;

public interface IExecucaoRepository
{
    Task<Execucao?> ObterAsync(Guid ordemServicoId, CancellationToken ct = default);

    /// <summary>Execucoes em andamento (NaFila, EmDiagnostico, EmReparo).</summary>
    Task<IReadOnlyList<Execucao>> ListarEmAndamentoAsync(CancellationToken ct = default);

    /// <summary>Upsert do documento inteiro (chave = OrdemServicoId).</summary>
    Task SalvarAsync(Execucao execucao, CancellationToken ct = default);
}

public static class MongoMapeamento
{
    private static readonly object Lock = new();
    private static bool _registrado;

    /// <summary>Guid como UUID padrao, enums como string e Id do agregado como _id.</summary>
    public static void Registrar()
    {
        lock (Lock)
        {
            if (_registrado) return;
            BsonSerializer.TryRegisterSerializer(new GuidSerializer(GuidRepresentation.Standard));
            ConventionRegistry.Register("oficina-execucao",
                new ConventionPack { new EnumRepresentationConvention(BsonType.String), new IgnoreExtraElementsConvention(true) },
                t => t.Namespace?.StartsWith("OficinaExecucao") == true);
            _registrado = true;
        }
    }
}

// Adaptador fino sobre o driver: exige um servidor MongoDB real, por isso fica fora da cobertura.
// O mapeamento BSON (MongoMapeamento) e coberto por teste de round-trip sem servidor.
[ExcludeFromCodeCoverage]
public class MongoExecucaoRepository : IExecucaoRepository
{
    private readonly IMongoCollection<Execucao> _colecao;

    public MongoExecucaoRepository(IMongoDatabase database)
    {
        _colecao = database.GetCollection<Execucao>("execucoes");
        _colecao.Indexes.CreateOne(new CreateIndexModel<Execucao>(
            Builders<Execucao>.IndexKeys.Ascending(e => e.Status).Ascending(e => e.CriadaEm)));
    }

    public async Task<Execucao?> ObterAsync(Guid ordemServicoId, CancellationToken ct = default) =>
        await _colecao.Find(e => e.Id == ordemServicoId).FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<Execucao>> ListarEmAndamentoAsync(CancellationToken ct = default) =>
        await _colecao.Find(Builders<Execucao>.Filter.In(e => e.Status, Execucao.StatusEmAndamento))
            .SortBy(e => e.CriadaEm)
            .ToListAsync(ct);

    public Task SalvarAsync(Execucao execucao, CancellationToken ct = default) =>
        _colecao.ReplaceOneAsync(e => e.Id == execucao.Id, execucao, new ReplaceOptions { IsUpsert = true }, ct);
}

[ExcludeFromCodeCoverage]
public class MongoHealthCheck(IMongoDatabase database) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await database.RunCommandAsync<BsonDocument>(new BsonDocument("ping", 1), cancellationToken: ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("MongoDB indisponivel", ex);
        }
    }
}

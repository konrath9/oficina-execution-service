using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using OficinaExecucao.Api.Infra;
using OficinaExecucao.Domain;

namespace OficinaExecucao.Tests;

public class InMemoryExecucaoRepository : IExecucaoRepository
{
    private readonly Dictionary<Guid, Execucao> _dados = new();

    public Task<Execucao?> ObterAsync(Guid ordemServicoId, CancellationToken ct = default)
    {
        lock (_dados) return Task.FromResult(_dados.GetValueOrDefault(ordemServicoId));
    }

    public Task<IReadOnlyList<Execucao>> ListarEmAndamentoAsync(CancellationToken ct = default)
    {
        lock (_dados)
            return Task.FromResult<IReadOnlyList<Execucao>>(
                _dados.Values.Where(e => Execucao.StatusEmAndamento.Contains(e.Status)).ToList());
    }

    public Task SalvarAsync(Execucao execucao, CancellationToken ct = default)
    {
        lock (_dados) _dados[execucao.Id] = execucao;
        return Task.CompletedTask;
    }
}

/// <summary>API real com repositorio em memoria no lugar do MongoDB e test harness no lugar do RabbitMQ.</summary>
public class ApiFactory : WebApplicationFactory<Program>
{
    public InMemoryExecucaoRepository Repositorio { get; } = new();

    public ITestHarness Harness => Services.GetTestHarness();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IExecucaoRepository>();
            services.AddSingleton<IExecucaoRepository>(Repositorio);
            services.Configure<HealthCheckServiceOptions>(o => o.Registrations.Clear());
            services.AddMassTransitTestHarness();
        });

    public HttpClient Cliente(string? perfil)
    {
        var client = CreateClient();
        if (perfil is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token(perfil));
        return client;
    }

    private static string Token(string perfil)
    {
        var chave = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("OficinaMecanica@SecretKey#2024!MustBe32CharsMin"));
        var token = new JwtSecurityToken(
            issuer: "OficinaMecanicaAPI",
            audience: "OficinaMecanicaClients",
            claims: new[] { new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, perfil) },
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(chave, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public static class Dados
{
    public static Execucao NovaExecucao(int servicos = 2) =>
        new(Guid.NewGuid(), "OS-2026-0001",
            Enumerable.Range(1, servicos).Select(i => new ServicoItem(Guid.NewGuid(), $"Servico {i}", 1)).ToList());

    public static Execucao EmReparo(int servicos = 2)
    {
        var e = NovaExecucao(servicos);
        e.IniciarDiagnostico();
        e.IniciarReparo();
        return e;
    }
}

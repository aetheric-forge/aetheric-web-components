using Aetheric.Provisioning.Application;
using Aetheric.Provisioning.Definitions;
using Aetheric.Provisioning.Engine;
using Aetheric.Provisioning.Persistence;
using AethericForge.Runtime.Abstractions.Interfaces.Post.Providers;
using AethericForge.Runtime.Institutions.Campus;
using AethericForge.Runtime.Providers.Post.RabbitMq;
using Aetheric.Provisioning.Worker;

var builder = Host.CreateApplicationBuilder(args);

// Built directly rather than resolved through DI: AddPostSubscription needs a concrete consumer
// instance up front, before the container exists to resolve one from.
var postProvider = new RabbitMqPostProvider(ProvisioningPost.Domain, BuildRabbitMqUrl(builder.Configuration));
builder.Services.AddSingleton<IPostProvider>(postProvider);

var definitionSource = new PublicGitHubSource(PublicGitHubSource.CreateHttpClient());
var reader = new InstitutionYamlReader();
// No real IResourceProvider is wired here yet - Stage 6 territory. An empty provider list
// means every owned resource fails planning with provider.unsupported, which is the correct,
// expected result until real providers exist.
var providers = Array.Empty<IResourceProvider>();
var planner = new ProvisioningPlanner(providers);
var dataDirectory = builder.Configuration["Provisioning:DataDirectory"] ?? Path.Combine(AppContext.BaseDirectory, "data");
var secretKey = new byte[32]; // Ephemeral: no plan reaches execution today, so no secret is ever written.
var engine = new ProvisioningEngine(
    providers,
    new NoParentCapabilityResolver(),
    new FileRunStateStore(Path.Combine(dataDirectory, "run-state")),
    new EncryptedFileSecretStore(Path.Combine(dataDirectory, "secrets"), secretKey));

builder.Services.AddPostSubscription(
    ProvisioningPost.RequestReference(),
    new CampusDeploymentRequestConsumer(postProvider, definitionSource, reader, planner, engine));

var host = builder.Build();
await host.RunAsync();

static string BuildRabbitMqUrl(IConfiguration configuration)
{
    var useSsl = configuration.GetValue("RabbitMq:Ssl", false);
    var uriBuilder = new UriBuilder
    {
        Scheme = useSsl ? "amqps" : "amqp",
        Host = Required(configuration, "RabbitMq:Host"),
        Port = configuration.GetValue<int?>("RabbitMq:Port") ?? (useSsl ? 5671 : 5672),
        UserName = Required(configuration, "RabbitMq:Username"),
        Password = Required(configuration, "RabbitMq:Password"),
        Path = Uri.EscapeDataString(Required(configuration, "RabbitMq:VirtualHost"))
    };
    return uriBuilder.Uri.ToString();
}

static string Required(IConfiguration configuration, string key) =>
    !string.IsNullOrWhiteSpace(configuration[key])
        ? configuration[key]!
        : throw new InvalidOperationException($"{key} is required.");

using System.Threading.Tasks;
using Azure.Identity;
using Azure.Messaging;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Bogus;
using FluentAssertions.Extensions;
using Hypothesist;
using Rebus.Activation;
using Rebus.Config;
using Xunit;
using Xunit.Abstractions;

namespace CloudEventify.Rebus.IntegrationTests;

public class ToAzureServiceBus : IAsyncLifetime
{
    private const string ConnectionString = "sbmanuel.servicebus.windows.net";
    private const string Topic = "io.cloudevents.demo.user.loggedIn";
    private const string Subscription = "integration-test";
    private readonly ITestOutputHelper _output;

    public ToAzureServiceBus(ITestOutputHelper output) => 
        _output = output;

    /// <summary>
    /// Role assignment: Azure Service Bus Data Owner
    /// </summary>
    [Fact]
    public async Task Do()
    {
        // Arrange
        var message = Message();
        var hypothesis = Hypothesis.For<CloudEvent>()
            .Any(m => m.Source == "jsdflkjsdf")
            .Any(x => x.Data.ToString().Contains(message.Id))
            .Any(x => x.Subject != null)
            .Any(x => x.ExtensionAttributes.ContainsKey("traceparent"))
            .Any(x => x.Source == "jsdflkjsdf");

        using var activator = new BuiltinHandlerActivator(); // not used, but testing the queue's address on the cloud event
        using var subscriber = Configure.With(activator)
            .Transport(t => t.UseAzureServiceBus($"Endpoint={ConnectionString}", "jsdflkjsdf", new DefaultAzureCredential()))
            .Options(o => o
                .UseCustomTypeNameForTopicName()
                .UseSenderAddress("this-should-not-override-the-queue-name"))
            .Serialization(s => s
                .UseCloudEvents()
                .AddWithCustomName<UserLoggedIn>(Topic))
            .Logging(l => l.MicrosoftExtensionsLogging(_output.ToLoggerFactory()))
            .Start();

        // Act
        await subscriber.Publish(message);
        
        // Assert
        await Receive(ConnectionString, Topic, Subscription, hypothesis);
    }
    
    [Fact]
    public async Task OneWay()
    {
        // Arrange
        var message = Message();
        var hypothesis = Hypothesis
            .For<CloudEvent>()
            .Any(x => x.Source == "cloudeventify:rebus")
            .Any(x => x.Data.ToString().Contains(message.Id));

        using var subscriber = Configure.OneWayClient()
            .Transport(t => t
                .UseNativeHeaders()
                .UseAzureServiceBusAsOneWayClient($"Endpoint={ConnectionString}", new DefaultAzureCredential()))
            .Options(o => o.UseCustomTypeNameForTopicName())
            .Serialization(s => s
                .UseCloudEvents()
                .AddWithCustomName<UserLoggedIn>(Topic))
            .Logging(l => l.MicrosoftExtensionsLogging(_output.ToLoggerFactory()))
            .Start();

        // Act
        await subscriber.Publish(message);
        
        // Assert
        await Receive(ConnectionString, Topic, Subscription, hypothesis);
    }
    
    [Fact]
    public async Task OneWayUseSourceAddress()
    {
        // Arrange
        var message = Message();
        var hypothesis = Hypothesis
            .For<CloudEvent>()
            .Any(x => x.Source == "my-custom-source")
            .Any(x => x.Data.ToString().Contains(message.Id));

        using var subscriber = Configure.OneWayClient()
            .Transport(t => t
                .UseNativeHeaders()
                .UseAzureServiceBusAsOneWayClient($"Endpoint={ConnectionString}", new DefaultAzureCredential()))
            .Options(o => o
                .UseCustomTypeNameForTopicName()
                .UseSenderAddress("my-custom-source"))
            .Options(o => o.LogPipeline(true))
            .Serialization(s => s
                .UseCloudEvents()
                .AddWithCustomName<UserLoggedIn>(Topic))
            .Logging(l => l.MicrosoftExtensionsLogging(_output.ToLoggerFactory()))
            .Start();

        // Act
        await subscriber.Publish(message);
        
        // Assert
        await Receive(ConnectionString, Topic, Subscription, hypothesis);
    }

    /// <summary>
    /// Copied from: https://learn.microsoft.com/en-us/dotnet/api/overview/azure/service-bus?view=azure-dotnet#code-example
    /// </summary>
    private static async Task Receive(string connectionString, string topic, string subscription,
        IHypothesis<CloudEvent> hypothesis)
    {
        await using var client = new ServiceBusClient(connectionString, new DefaultAzureCredential());
        await using var receiver = client.CreateProcessor(topic, subscription);
        receiver.ProcessMessageAsync += async x => 
            await hypothesis.Test(CloudEvent.Parse(x.Message.Body));
        receiver.ProcessErrorAsync += e => throw e.Exception;

        await receiver.StartProcessingAsync();
        await hypothesis.Validate(30.Seconds());
    }

    public record UserLoggedIn(string Id);
        
    private static UserLoggedIn Message() => 
        new Faker<UserLoggedIn>()
            .CustomInstantiator(f => new(f.Random.Hash()))
            .Generate();

    async Task IAsyncLifetime.InitializeAsync()
    {
        var admin = new ServiceBusAdministrationClient(ConnectionString, new DefaultAzureCredential());
        if (await admin.TopicExistsAsync(Topic))
        {
            await admin.DeleteTopicAsync(Topic);
        }

        await admin.CreateTopicAsync(Topic);
        await admin.CreateSubscriptionAsync(Topic, Subscription);
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        var admin = new ServiceBusAdministrationClient(ConnectionString, new DefaultAzureCredential());
        await admin.DeleteTopicAsync(Topic);
    }
}
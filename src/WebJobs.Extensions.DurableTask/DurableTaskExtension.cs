// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DurableTask.AzureStorage;
using DurableTask.Core;
using DurableTask.Core.Exceptions;
using DurableTask.Core.History;
using DurableTask.Core.Middleware;
using Microsoft.Azure.WebJobs.Description;
#if !FUNCTIONS_V1
using Microsoft.Azure.WebJobs.Extensions.DurableTask.Correlation;
using Microsoft.Azure.WebJobs.Host.Scale;
#endif
using Microsoft.Azure.WebJobs.Extensions.DurableTask.Listener;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.Host.Config;
using Microsoft.Azure.WebJobs.Host.Executors;
using Microsoft.Azure.WebJobs.Logging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Microsoft.Azure.WebJobs.Extensions.DurableTask
{
    /// <summary>
    /// Configuration for the Durable Functions extension.
    /// </summary>
#if !FUNCTIONS_V1
    [Extension("DurableTask", "DurableTask")]
#endif
    public class DurableTaskExtension :
        IExtensionConfigProvider,
        IDisposable,
        IAsyncConverter<HttpRequestMessage, HttpResponseMessage>,
        INameVersionObjectManager<TaskOrchestration>,
        INameVersionObjectManager<TaskActivity>
    {
        private const string DefaultProvider = AzureStorageDurabilityProviderFactory.ProviderName;

        internal static readonly string LoggerCategoryName = LogCategories.CreateTriggerCategory("DurableTask");

        // Creating client objects is expensive, so we cache them when the attributes match.
        // Note that DurableClientAttribute defines a custom equality comparer.
        private readonly ConcurrentDictionary<DurableClientAttribute, DurableClient> cachedClients =
            new ConcurrentDictionary<DurableClientAttribute, DurableClient>();

        private readonly ConcurrentDictionary<FunctionName, RegisteredFunctionInfo> knownOrchestrators =
            new ConcurrentDictionary<FunctionName, RegisteredFunctionInfo>();

        private readonly ConcurrentDictionary<FunctionName, RegisteredFunctionInfo> knownEntities =
            new ConcurrentDictionary<FunctionName, RegisteredFunctionInfo>();

        private readonly ConcurrentDictionary<FunctionName, RegisteredFunctionInfo> knownActivities =
            new ConcurrentDictionary<FunctionName, RegisteredFunctionInfo>();

        private readonly AsyncLock taskHubLock = new AsyncLock();
#if FUNCTIONS_V2_OR_GREATER
#pragma warning disable CS0169
        private readonly ITelemetryActivator telemetryActivator;
#pragma warning restore CS0169
#endif
#if FUNCTIONS_V3_OR_GREATER
        private readonly LocalGrpcListener localGrpcListener;
#endif
        private readonly bool isOptionsConfigured;
#pragma warning disable CS0612 // Type or member is obsolete
#pragma warning disable SA1401 // Fields should be private
        internal IPlatformInformation PlatformInformationService;
#pragma warning restore SA1401 // Fields should be private
#pragma warning restore CS0612 // Type or member is obsolete
        private IDurabilityProviderFactory durabilityProviderFactory;
        private INameResolver nameResolver;
        private ILoggerFactory loggerFactory;
        private DurabilityProvider defaultDurabilityProvider;
        private TaskHubWorker taskHubWorker;
        private bool isTaskHubWorkerStarted;
        private HttpClient durableHttpClient;
        private EventSourceListener eventSourceListener;

#if FUNCTIONS_V1
        private IConnectionInfoResolver connectionInfoResolver;

        /// <summary>
        /// Obsolete. Please use an alternate constructor overload.
        /// </summary>
        [Obsolete("The default constructor is obsolete and will be removed in future versions")]
        public DurableTaskExtension()
        {
            // Options initialization happens later
            this.Options = new DurableTaskOptions();
            this.isOptionsConfigured = false;
        }
#endif

        /// <summary>
        /// Initializes a new instance of the <see cref="DurableTaskExtension"/>.
        /// </summary>
        /// <param name="options">The configuration options for this extension.</param>
        /// <param name="loggerFactory">The logger factory used for extension-specific logging and orchestration tracking.</param>
        /// <param name="nameResolver">The name resolver to use for looking up application settings.</param>
        /// <param name="orchestrationServiceFactories">The factories used to create orchestration service based on the configured storage provider.</param>
        /// <param name="durableHttpMessageHandlerFactory">The HTTP message handler that handles HTTP requests and HTTP responses.</param>
        /// <param name="hostLifetimeService">The host shutdown notification service for detecting and reacting to host shutdowns.</param>
        /// <param name="lifeCycleNotificationHelper">The lifecycle notification helper used for custom orchestration tracking.</param>
        /// <param name="messageSerializerSettingsFactory">The factory used to create <see cref="JsonSerializerSettings"/> for message settings.</param>
        /// <param name="errorSerializerSettingsFactory">The factory used to create <see cref="JsonSerializerSettings"/> for error settings.</param>
        /// <param name="webhookProvider">Provides webhook urls for HTTP management APIs.</param>
        /// <param name="telemetryActivator">The activator of DistributedTracing. .netstandard2.0 only.</param>
        /// <param name="platformInformationService">The platform information provider to inspect the OS, app service plan, and other enviroment information.</param>
        public DurableTaskExtension(
            IOptions<DurableTaskOptions> options,
            ILoggerFactory loggerFactory,
            INameResolver nameResolver,
            IEnumerable<IDurabilityProviderFactory> orchestrationServiceFactories,
            IApplicationLifetimeWrapper hostLifetimeService,
            IDurableHttpMessageHandlerFactory durableHttpMessageHandlerFactory = null,
            ILifeCycleNotificationHelper lifeCycleNotificationHelper = null,
            IMessageSerializerSettingsFactory messageSerializerSettingsFactory = null,
#pragma warning disable CS0612 // Type or member is obsolete
            IPlatformInformation platformInformationService = null,
#pragma warning restore CS0612 // Type or member is obsolete
#if FUNCTIONS_V2_OR_GREATER
            IErrorSerializerSettingsFactory errorSerializerSettingsFactory = null,
#pragma warning disable CS0618 // Type or member is obsolete
            IWebHookProvider webhookProvider = null,
#pragma warning restore CS0618 // Type or member is obsolete
            ITelemetryActivator telemetryActivator = null)
#else
            IErrorSerializerSettingsFactory errorSerializerSettingsFactory = null)
#endif
        {
            // Options will be null in Functions v1 runtime - populated later.
            this.Options = options?.Value ?? new DurableTaskOptions();
            this.nameResolver = nameResolver ?? throw new ArgumentNullException(nameof(nameResolver));
            this.loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            this.PlatformInformationService = platformInformationService ?? throw new ArgumentNullException(nameof(platformInformationService));
            this.ResolveAppSettingOptions();

            ILogger logger = loggerFactory.CreateLogger(LoggerCategoryName);

            this.TraceHelper = new EndToEndTraceHelper(logger, this.Options.Tracing.TraceReplayEvents);
            this.LifeCycleNotificationHelper = lifeCycleNotificationHelper ?? this.CreateLifeCycleNotificationHelper();
            this.durabilityProviderFactory = this.GetDurabilityProviderFactory(this.Options, logger, orchestrationServiceFactories);
            this.defaultDurabilityProvider = this.durabilityProviderFactory.GetDurabilityProvider();
            this.isOptionsConfigured = true;

            if (durableHttpMessageHandlerFactory == null)
            {
                durableHttpMessageHandlerFactory = new DurableHttpMessageHandlerFactory();
            }

            DurableHttpClientFactory durableHttpClientFactory = new DurableHttpClientFactory();
            this.durableHttpClient = durableHttpClientFactory.GetClient(durableHttpMessageHandlerFactory);

            this.MessageDataConverter = CreateMessageDataConverter(messageSerializerSettingsFactory);
            this.ErrorDataConverter = this.CreateErrorDataConverter(errorSerializerSettingsFactory);

            this.TypedCodeProvider = new TypedCodeProvider();
            this.TypedCodeProvider.Initialize();

            this.HttpApiHandler = new HttpApiHandler(this, logger);
#if !FUNCTIONS_V1
            // This line ensure every time we need the webhook URI, we get it directly from the
            // function runtime, which has the most up-to-date knowledge about the site hostname.
            Func<Uri> webhookDelegate = () => webhookProvider.GetUrl(this);
            this.HttpApiHandler.RegisterWebhookProvider(
                this.Options.WebhookUriProviderOverride ??
                webhookDelegate);
#endif

            this.HostLifetimeService = hostLifetimeService;

#if !FUNCTIONS_V1
            // The RPC server is started when the extension is initialized.
            // The RPC server is stopped when the host has finished shutting down.
            this.HostLifetimeService.OnStopped.Register(this.StopLocalHttpServer);
            this.telemetryActivator = telemetryActivator;
            this.telemetryActivator?.Initialize(logger);
#endif

            // Starting with .NET isolated and Java, we have a more efficient out-of-process
            // function invocation protocol. Other languages will use the existing protocol.
            WorkerRuntimeType runtimeType = this.PlatformInformationService.GetWorkerRuntimeType();
            if (runtimeType == WorkerRuntimeType.DotNetIsolated || runtimeType == WorkerRuntimeType.Java)
            {
                this.OutOfProcProtocol = OutOfProcOrchestrationProtocol.MiddlewarePassthrough;
#if FUNCTIONS_V3_OR_GREATER
                this.localGrpcListener = new LocalGrpcListener(this, this.defaultDurabilityProvider);
                this.HostLifetimeService.OnStopped.Register(this.StopLocalGrpcServer);
#endif
            }
            else
            {
                this.OutOfProcProtocol = OutOfProcOrchestrationProtocol.OrchestratorShim;
            }
        }

#if FUNCTIONS_V1
        internal DurableTaskExtension(
            IOptions<DurableTaskOptions> options,
            ILoggerFactory loggerFactory,
            INameResolver nameResolver,
            IEnumerable<IDurabilityProviderFactory> orchestrationServiceFactories,
            IConnectionInfoResolver connectionInfoResolver,
            IApplicationLifetimeWrapper shutdownNotification,
            IDurableHttpMessageHandlerFactory durableHttpMessageHandlerFactory,
#pragma warning disable CS0612 // Type or member is obsolete
            IPlatformInformation platformInformationService)
#pragma warning restore CS0612 // Type or member is obsolete

            : this(options, loggerFactory, nameResolver, orchestrationServiceFactories, shutdownNotification, durableHttpMessageHandlerFactory)
        {
            this.connectionInfoResolver = connectionInfoResolver;
        }

        /// <summary>
        /// Gets or sets default task hub name to be used by all <see cref="IDurableClient"/>, <see cref="IDurableOrchestrationClient"/>, <see cref="IDurableEntityClient"/>,
        /// <see cref="IDurableOrchestrationContext"/>, and <see cref="IDurableActivityContext"/> instances.
        /// </summary>
        /// <remarks>
        /// A task hub is a logical grouping of storage resources. Alternate task hub names can be used to isolate
        /// multiple Durable Functions applications from each other, even if they are using the same storage backend.
        /// </remarks>
        /// <value>The name of the default task hub.</value>
        public string HubName
        {
            get { return this.Options.HubName; }
            set { this.Options.HubName = value; }
        }
#endif

        internal DurableTaskOptions Options { get; }

        internal HttpApiHandler HttpApiHandler { get; private set; }

        internal ILifeCycleNotificationHelper LifeCycleNotificationHelper { get; private set; }

        internal EndToEndTraceHelper TraceHelper { get; private set; }

        internal MessagePayloadDataConverter MessageDataConverter { get; private set; }

        internal MessagePayloadDataConverter ErrorDataConverter { get; private set; }

        internal TypedCodeProvider TypedCodeProvider { get; private set; }

        internal TimeSpan MessageReorderWindow
            => this.defaultDurabilityProvider.GuaranteesOrderedDelivery ? TimeSpan.Zero : TimeSpan.FromMinutes(this.Options.EntityMessageReorderWindowInMinutes);

        internal IApplicationLifetimeWrapper HostLifetimeService { get; } = HostLifecycleService.NoOp;

        internal OutOfProcOrchestrationProtocol OutOfProcProtocol { get; }

        internal static MessagePayloadDataConverter CreateMessageDataConverter(IMessageSerializerSettingsFactory messageSerializerSettingsFactory)
        {
            bool isDefault;
            if (messageSerializerSettingsFactory == null)
            {
                messageSerializerSettingsFactory = new MessageSerializerSettingsFactory();
            }

            isDefault = messageSerializerSettingsFactory is MessageSerializerSettingsFactory;

            return new MessagePayloadDataConverter(messageSerializerSettingsFactory.CreateJsonSerializerSettings(), isDefault);
        }

        private MessagePayloadDataConverter CreateErrorDataConverter(IErrorSerializerSettingsFactory errorSerializerSettingsFactory)
        {
            bool isDefault;
            if (errorSerializerSettingsFactory == null)
            {
                errorSerializerSettingsFactory = new ErrorSerializerSettingsFactory();
            }

            isDefault = errorSerializerSettingsFactory is ErrorSerializerSettingsFactory;

            return new MessagePayloadDataConverter(errorSerializerSettingsFactory.CreateJsonSerializerSettings(), isDefault);
        }

        private IDurabilityProviderFactory GetDurabilityProviderFactory(DurableTaskOptions options, ILogger logger, IEnumerable<IDurabilityProviderFactory> orchestrationServiceFactories)
        {
            bool storageTypeIsConfigured = options.StorageProvider.TryGetValue("type", out object storageType);

            if (!storageTypeIsConfigured)
            {
                try
                {
                    IDurabilityProviderFactory defaultFactory = orchestrationServiceFactories.First(f => f.Name.Equals(DefaultProvider));
                    logger.LogInformation($"Using the default storage provider: {DefaultProvider}.");
                    return defaultFactory;
                }
                catch (InvalidOperationException e)
                {
                    throw new InvalidOperationException($"Couldn't find the default storage provider: {DefaultProvider}.", e);
                }
            }

            try
            {
                IDurabilityProviderFactory selectedFactory = orchestrationServiceFactories.First(f => string.Equals(f.Name, storageType.ToString(), StringComparison.OrdinalIgnoreCase));
                logger.LogInformation($"Using the {storageType} storage provider.");
                return selectedFactory;
            }
            catch (InvalidOperationException e)
            {
                IList<string> factoryNames = orchestrationServiceFactories.Select(f => f.Name).ToList();
                throw new InvalidOperationException($"Storage provider type ({storageType}) was not found. Available storage providers: {string.Join(", ", factoryNames)}.", e);
            }
        }

        internal string GetBackendInfo()
        {
            return this.defaultDurabilityProvider.GetBackendInfo();
        }

        /// <summary>
        /// Internal initialization call from the WebJobs host.
        /// </summary>
        /// <param name="context">Extension context provided by WebJobs.</param>
        void IExtensionConfigProvider.Initialize(ExtensionConfigContext context)
        {
#if !FUNCTIONS_V1
            // Functions V1 is not supported in linux, so this is conditionally compiled
            // We initialize linux logging early on in case any initialization steps below were to trigger a log event.
            if (this.PlatformInformationService.GetOperatingSystem() == OperatingSystem.Linux)
            {
                this.InitializeLinuxLogging();
            }
#endif

            ConfigureLoaderHooks();

            // Functions V1 has it's configuration initialized at startup time (now).
            // For Functions V2 (and for some unit tests) configuration happens earlier in the pipeline.
            if (!this.isOptionsConfigured)
            {
                this.InitializeForFunctionsV1(context);
            }

            // Throw if any of the configured options are invalid
            this.Options.Validate(this.nameResolver, this.TraceHelper);

#pragma warning disable CS0618 // Type or member is obsolete

            // Invoke webhook handler to make functions runtime register extension endpoints.
            var initialWebhookUri = context.GetWebhookHandler();

#if FUNCTIONS_V1
            // In Functions V1, there is no notion of an IWebhookProvider that
            // we can dynamically call to fetch the webhook URI, and since context.GetWebhookHandler()
            // only works in the scope of the Initialize() function, we just have to live with the static URI
            // we grab now.
            Func<Uri> staticWebhookHandler = () => initialWebhookUri;
            this.HttpApiHandler.RegisterWebhookProvider(
                this.Options.WebhookUriProviderOverride ??
                staticWebhookHandler);
#endif
#pragma warning restore CS0618 // Type or member is obsolete

            this.TraceConfigurationSettings();

            var bindings = new BindingHelper(this);

            // Note that the order of the rules is important
            var rule = context.AddBindingRule<DurableClientAttribute>()
                .AddConverter<string, StartOrchestrationArgs>(bindings.StringToStartOrchestrationArgs)
                .AddConverter<JObject, StartOrchestrationArgs>(bindings.JObjectToStartOrchestrationArgs)
                .AddConverter<IDurableClient, string>(bindings.DurableOrchestrationClientToString);

            rule.BindToCollector<StartOrchestrationArgs>(bindings.CreateAsyncCollector);
            rule.BindToInput<IDurableOrchestrationClient>(this.GetClient);
            rule.BindToInput<IDurableEntityClient>(this.GetClient);
            rule.BindToInput<IDurableClient>(this.GetClient);

            if (this.TypedCodeProvider.IsInitialized)
            {
                rule.Bind(new TypedDurableClientBindingProvider(this.TypedCodeProvider, this.GetClient));
            }

            // We add a binding rule to support the now deprecated `orchestrationClient` binding
            // A cleaner solution would be to have the prior rule have an OR-style selector between
            // DurableClientAttribute and OrchestrationClientAttribute, but it's unclear if that's
            // possible (for now).
#pragma warning disable CS0618 // Type or member is obsolete
            var backwardsCompRule = context.AddBindingRule<OrchestrationClientAttribute>()
#pragma warning restore CS0618 // Type or member is obsolete
                .AddConverter<string, StartOrchestrationArgs>(bindings.StringToStartOrchestrationArgs)
                .AddConverter<JObject, StartOrchestrationArgs>(bindings.JObjectToStartOrchestrationArgs)
                .AddConverter<IDurableClient, string>(bindings.DurableOrchestrationClientToString);

            backwardsCompRule.BindToCollector<StartOrchestrationArgs>(bindings.CreateAsyncCollector);
            backwardsCompRule.BindToInput<IDurableOrchestrationClient>(this.GetClient);
            backwardsCompRule.BindToInput<IDurableEntityClient>(this.GetClient);
            backwardsCompRule.BindToInput<IDurableClient>(this.GetClient);

            if (this.TypedCodeProvider.IsInitialized)
            {
                backwardsCompRule.Bind(new TypedDurableClientBindingProvider(this.TypedCodeProvider, this.GetClient));
            }

            string connectionName = this.durabilityProviderFactory is AzureStorageDurabilityProviderFactory azureStorageDurabilityProviderFactory
                ? azureStorageDurabilityProviderFactory.DefaultConnectionName
                : null;

            context.AddBindingRule<OrchestrationTriggerAttribute>()
                .BindToTrigger(new OrchestrationTriggerAttributeBindingProvider(this, connectionName));

            context.AddBindingRule<ActivityTriggerAttribute>()
                .BindToTrigger(new ActivityTriggerAttributeBindingProvider(this, connectionName));

            context.AddBindingRule<EntityTriggerAttribute>()
                .BindToTrigger(new EntityTriggerAttributeBindingProvider(this, connectionName));

            this.taskHubWorker = new TaskHubWorker(this.defaultDurabilityProvider, this, this, this.loggerFactory);

            // Add middleware to the DTFx dispatcher so that we can inject our own logic
            // into and customize the orchestration execution pipeline.
            // Note that the order of the middleware added determines the order in which it executes.
            if (this.OutOfProcProtocol == OutOfProcOrchestrationProtocol.MiddlewarePassthrough)
            {
#if FUNCTIONS_V3_OR_GREATER
                // This is a newer, more performant flavor of orchestration/activity middleware that is being
                // enabled for newer language runtimes. Support for entities in this model is TBD.
                var ooprocMiddleware = new OutOfProcMiddleware(this);
                this.taskHubWorker.AddActivityDispatcherMiddleware(ooprocMiddleware.CallActivityAsync);
                this.taskHubWorker.AddOrchestrationDispatcherMiddleware(ooprocMiddleware.CallOrchestratorAsync);
#else
                // This can happen if, for example, a Java user tries to use Durable Functions while targeting V2 or V3 extension bundles
                // because those bundles target .NET Core 2.2, which doesn't support the gRPC libraries used in the modern out-of-proc implementation.
                throw new PlatformNotSupportedException(
                    "This project type is not supported on this version of the Azure Functions runtime. Please upgrade to Azure Functions V3 or higher. " +
                    "If you are using a language that supports extension bundles, please use extension bundles V4 or higher. " +
                    "For more information on Azure Functions versions, see https://docs.microsoft.com/azure/azure-functions/functions-versions. " +
                    "For more information on extension bundles, see https://docs.microsoft.com/azure/azure-functions/functions-bindings-register#extension-bundles.");
#endif
            }
            else
            {
                // This is the older middleware implementation that is currently in use for in-process .NET
                // and the older out-of-proc languages, like Node.js, Python, and PowerShell.
                this.taskHubWorker.AddActivityDispatcherMiddleware(this.ActivityMiddleware);
                this.taskHubWorker.AddOrchestrationDispatcherMiddleware(this.EntityMiddleware);
                this.taskHubWorker.AddOrchestrationDispatcherMiddleware(this.OrchestrationMiddleware);
            }

            // The RPC server needs to be started sometime before any functions can be triggered
            // and this is the latest point in the pipeline available to us.
#if FUNCTIONS_V3_OR_GREATER
            if (this.OutOfProcProtocol == OutOfProcOrchestrationProtocol.MiddlewarePassthrough)
            {
                this.StartLocalGrpcServer();
            }
#endif
#if FUNCTIONS_V2_OR_GREATER
            if (this.OutOfProcProtocol == OutOfProcOrchestrationProtocol.OrchestratorShim)
            {
                this.StartLocalHttpServer();
            }
#endif
        }

        internal string GetLocalRpcAddress()
        {
#if FUNCTIONS_V3_OR_GREATER
            if (this.OutOfProcProtocol == OutOfProcOrchestrationProtocol.MiddlewarePassthrough)
            {
                return this.localGrpcListener.ListenAddress;
            }
#endif

            return this.HttpApiHandler.GetBaseUrl();
        }

        /// <summary>
        /// Initializes the logging service for App Service if it detects that we are running in
        /// the linux platform.
        /// </summary>
        private void InitializeLinuxLogging()
        {
            // Determine host platform
            bool inConsumption = this.PlatformInformationService.IsInConsumptionPlan();

            string tenant = this.PlatformInformationService.GetLinuxTenant();
            string stampName = this.PlatformInformationService.GetLinuxStampName();
            string containerName = this.PlatformInformationService.GetContainerName();

            // If running in linux, initialize the EventSource listener with the appropiate logger.
            LinuxAppServiceLogger linuxLogger;
            if (!inConsumption)
            {
                linuxLogger = new LinuxAppServiceLogger(writeToConsole: false, containerName, tenant, stampName);
            }
            else
            {
                linuxLogger = new LinuxAppServiceLogger(writeToConsole: true, containerName, tenant, stampName);
            }

            // The logging service for linux works by capturing EventSource messages,
            // which our linux platform does not recognize, and logging them via a
            // different strategy such as writing to console or to a file.

            // Since our logging payload can be quite large, linux telemetry by default
            // disables verbose-level telemetry to avoid a performance hit.
            bool enableVerbose = this.Options.Tracing.AllowVerboseLinuxTelemetry;
            this.eventSourceListener = new EventSourceListener(linuxLogger, enableVerbose, this.TraceHelper, this.defaultDurabilityProvider.EventSourceName);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            // Not flushing the linux logger may lead to lost logs
            // 40 seconds timeout because we write normally every 30 seconds, so we're just
            // adding an extra 10 seconds to flush.
            LinuxAppServiceLogger.Logger?.Stop(TimeSpan.FromSeconds(40));
            this.HttpApiHandler?.Dispose();
            this.eventSourceListener?.Dispose();
        }

#if FUNCTIONS_V2_OR_GREATER
        private void StartLocalHttpServer()
        {
            bool? shouldEnable = this.Options.LocalRpcEndpointEnabled;
            if (!shouldEnable.HasValue)
            {
                WorkerRuntimeType runtimeType = this.PlatformInformationService.GetWorkerRuntimeType();
                shouldEnable = runtimeType switch
                {
                    // dotnet runs in process
                    WorkerRuntimeType.DotNet => false,

                    // dotnet-isolated and java use a gRPC server instead of the HTTP server
                    WorkerRuntimeType.DotNetIsolated => false,
                    WorkerRuntimeType.Java => false,

                    // everything else - assume the HTTP server
                    WorkerRuntimeType.Python => true,
                    WorkerRuntimeType.Node => true,
                    WorkerRuntimeType.PowerShell => true,
                    WorkerRuntimeType.Unknown => true,
                    _ => true,
                };
            }

            if (shouldEnable == true)
            {
                this.HttpApiHandler.StartLocalHttpServerAsync().GetAwaiter().GetResult();
            }
        }

        private void StopLocalHttpServer()
        {
            this.HttpApiHandler.StopLocalHttpServerAsync().GetAwaiter().GetResult();
        }
#endif

#if FUNCTIONS_V3_OR_GREATER
        private void StartLocalGrpcServer()
        {
            this.localGrpcListener.StartAsync().GetAwaiter().GetResult();
        }

        private void StopLocalGrpcServer()
        {
            this.localGrpcListener.StopAsync().GetAwaiter().GetResult();
        }
#endif

        private void ResolveAppSettingOptions()
        {
            if (this.Options == null)
            {
                throw new InvalidOperationException($"{nameof(this.Options)} must be set before resolving app settings.");
            }

            if (this.nameResolver == null)
            {
                throw new InvalidOperationException($"{nameof(this.nameResolver)} must be set before resolving app settings.");
            }

            if (this.nameResolver.TryResolveWholeString(this.Options.HubName, out string taskHubName))
            {
                // use the resolved task hub name
                this.Options.HubName = taskHubName;
            }
        }

        private void InitializeForFunctionsV1(ExtensionConfigContext context)
        {
#if FUNCTIONS_V1
            context.ApplyConfig(this.Options, "DurableTask");
            this.nameResolver = context.Config.NameResolver;
            this.loggerFactory = context.Config.LoggerFactory;
            this.ResolveAppSettingOptions();
            ILogger logger = this.loggerFactory.CreateLogger(LoggerCategoryName);
            this.TraceHelper = new EndToEndTraceHelper(logger, this.Options.Tracing.TraceReplayEvents);
            this.connectionInfoResolver = new WebJobsConnectionInfoProvider();
            this.PlatformInformationService = new DefaultPlatformInformation(this.nameResolver, this.loggerFactory);
            this.durabilityProviderFactory = new AzureStorageDurabilityProviderFactory(
                new OptionsWrapper<DurableTaskOptions>(this.Options),
                new AzureStorageAccountProvider(this.connectionInfoResolver),
                this.nameResolver,
                this.loggerFactory,
                this.PlatformInformationService);
            this.defaultDurabilityProvider = this.durabilityProviderFactory.GetDurabilityProvider();
            this.LifeCycleNotificationHelper = this.CreateLifeCycleNotificationHelper();
            var messageSerializerSettingsFactory = new MessageSerializerSettingsFactory();
            var errorSerializerSettingsFactory = new ErrorSerializerSettingsFactory();
            this.MessageDataConverter = new MessagePayloadDataConverter(messageSerializerSettingsFactory.CreateJsonSerializerSettings(), true);
            this.ErrorDataConverter = new MessagePayloadDataConverter(errorSerializerSettingsFactory.CreateJsonSerializerSettings(), true);
            this.HttpApiHandler = new HttpApiHandler(this, logger);
            this.TypedCodeProvider = new TypedCodeProvider();
            this.TypedCodeProvider.Initialize();
#endif
        }

        private void TraceConfigurationSettings()
        {
            this.Options.TraceConfiguration(
                this.TraceHelper,
                this.defaultDurabilityProvider.ConfigurationJson);
        }

        private ILifeCycleNotificationHelper CreateLifeCycleNotificationHelper()
        {
            // First: EventGrid
            if (this.Options.Notifications.EventGrid != null
                && (!string.IsNullOrEmpty(this.Options.Notifications.EventGrid.TopicEndpoint) || !string.IsNullOrEmpty(this.Options.Notifications.EventGrid.KeySettingName)))
            {
                return new EventGridLifeCycleNotificationHelper(this.Options, this.nameResolver, this.TraceHelper);
            }

            // Fallback: Disable Notification
            return new NullLifeCycleNotificationHelper();
        }

        /// <summary>
        /// Deletes all data stored in the current task hub.
        /// </summary>
        /// <returns>A task representing the async delete operation.</returns>
        public Task DeleteTaskHubAsync()
        {
            return this.defaultDurabilityProvider.DeleteAsync();
        }

        /// <summary>
        /// Called by the Durable Task Framework: Not used.
        /// </summary>
        /// <param name="creator">This parameter is not used.</param>
        void INameVersionObjectManager<TaskOrchestration>.Add(ObjectCreator<TaskOrchestration> creator)
        {
            throw new InvalidOperationException("Orchestrations should never be added explicitly.");
        }

        /// <summary>
        /// Called by the Durable Task Framework: Returns the specified <see cref="TaskOrchestration"/>.
        /// </summary>
        /// <param name="name">The name of the orchestration to return.</param>
        /// <param name="version">Not used.</param>
        /// <returns>An orchestration shim that delegates execution to an orchestrator function.</returns>
        TaskOrchestration INameVersionObjectManager<TaskOrchestration>.GetObject(string name, string version)
        {
            if (name.StartsWith("@"))
            {
                return new TaskEntityShim(this, this.defaultDurabilityProvider, name);
            }
            else
            {
                return new TaskOrchestrationShim(this, this.defaultDurabilityProvider, name);
            }
        }

        /// <summary>
        /// Called by the durable task framework: Not used.
        /// </summary>
        /// <param name="creator">This parameter is not used.</param>
        void INameVersionObjectManager<TaskActivity>.Add(ObjectCreator<TaskActivity> creator)
        {
            throw new InvalidOperationException("Activities should never be added explicitly.");
        }

        /// <summary>
        /// Called by the Durable Task Framework: Returns the specified <see cref="TaskActivity"/>.
        /// </summary>
        /// <param name="name">The name of the activity to return.</param>
        /// <param name="version">Not used.</param>
        /// <returns>An activity shim that delegates execution to an activity function.</returns>
        TaskActivity INameVersionObjectManager<TaskActivity>.GetObject(string name, string version)
        {
            if (IsDurableHttpTask(name))
            {
                return new TaskHttpActivityShim(this, this.durableHttpClient);
            }

            FunctionName activityFunction = new FunctionName(name);

            RegisteredFunctionInfo info;
            if (!this.knownActivities.TryGetValue(activityFunction, out info))
            {
                return new TaskNonexistentActivityShim(this, name);
            }

            return new TaskActivityShim(this, info.Executor, this.HostLifetimeService, name);
        }

        /// <summary>
        /// This DTFx activity middleware allows us to add context to the activity function shim
        /// before it actually starts running.
        /// </summary>
        /// <param name="dispatchContext">A property bag containing useful DTFx context.</param>
        /// <param name="next">The handler for running the next middleware in the pipeline.</param>
        private Task ActivityMiddleware(DispatchMiddlewareContext dispatchContext, Func<Task> next)
        {
            if (dispatchContext.GetProperty<TaskActivity>() is TaskActivityShim shim)
            {
                TaskScheduledEvent @event = dispatchContext.GetProperty<TaskScheduledEvent>();
                shim.SetTaskEventId(@event?.EventId ?? -1);
            }

            // Move to the next stage of the DTFx pipeline to trigger the activity shim.
            return next();
        }

        /// <summary>
        /// This DTFx orchestration middleware allows us to initialize Durable Functions-specific context
        /// and make the execution happen in a way that plays nice with the Azure Functions execution pipeline.
        /// </summary>
        /// <param name="dispatchContext">A property bag containing useful DTFx context.</param>
        /// <param name="next">The handler for running the next middleware in the pipeline.</param>
        private async Task OrchestrationMiddleware(DispatchMiddlewareContext dispatchContext, Func<Task> next)
        {
            TaskOrchestrationShim shim = dispatchContext.GetProperty<TaskOrchestration>() as TaskOrchestrationShim;
            if (shim == null)
            {
                // This is not an orchestration - skip.
                await next();
                return;
            }

            DurableOrchestrationContext context = (DurableOrchestrationContext)shim.Context;

            OrchestrationRuntimeState orchestrationRuntimeState = dispatchContext.GetProperty<OrchestrationRuntimeState>();

            if (orchestrationRuntimeState.ParentInstance != null)
            {
                context.ParentInstanceId = orchestrationRuntimeState.ParentInstance.OrchestrationInstance.InstanceId;
            }

            context.InstanceId = orchestrationRuntimeState.OrchestrationInstance?.InstanceId;
            context.ExecutionId = orchestrationRuntimeState.OrchestrationInstance?.ExecutionId;
            context.IsReplaying = orchestrationRuntimeState.ExecutionStartedEvent.IsPlayed;
            context.History = orchestrationRuntimeState.Events;
            context.RawInput = orchestrationRuntimeState.Input;

            RegisteredFunctionInfo info = shim.GetFunctionInfo();
            if (info == null)
            {
                string message = this.GetInvalidOrchestratorFunctionMessage(context.FunctionName);

                this.TraceHelper.ExtensionWarningEvent(
                    this.Options.HubName,
                    orchestrationRuntimeState.Name,
                    orchestrationRuntimeState.OrchestrationInstance.InstanceId,
                    message);

                Func<Task<OrchestrationFailureException>> nonExistentException = () => throw new OrchestrationFailureException(message);
                shim.SetFunctionInvocationCallback(nonExistentException);
                await next();
            }
            else
            {
                // 1. Start the functions invocation pipeline (billing, logging, bindings, and timeout tracking).
                WrappedFunctionResult result = await FunctionExecutionHelper.ExecuteFunctionInOrchestrationMiddleware(
                    info.Executor,
                    new TriggeredFunctionData
                    {
                        TriggerValue = context,

#pragma warning disable CS0618 // Approved for use by this extension
                        InvokeHandler = async userCodeInvoker =>
                        {
                            // We yield control to ensure this code is executed asynchronously relative to WebJobs.
                            // This ensures WebJobs is able to correctly cancel the invocation in the case of a timeout.

                            await Task.Yield();
                            context.ExecutorCalledBack = true;

                            // 2. Configure the shim with the inner invoker to execute the user code.
                            shim.SetFunctionInvocationCallback(userCodeInvoker);

                            // 3. Move to the next stage of the DTFx pipeline to trigger the orchestrator shim.
                            await next();

                            // 4. If an activity failed, indicate to the functions Host that this execution failed via an exception
                            if (context.IsCompleted && context.OrchestrationException != null)
                            {
                                context.OrchestrationException.Throw();
                            }
                        },
#pragma warning restore CS0618
                    },
                    shim,
                    context,
                    this.HostLifetimeService.OnStopping);

                if (result.ExecutionStatus == WrappedFunctionResult.FunctionResultStatus.FunctionsRuntimeError
                    || result.ExecutionStatus == WrappedFunctionResult.FunctionResultStatus.FunctionsHostStoppingError
                    || result.ExecutionStatus == WrappedFunctionResult.FunctionResultStatus.FunctionTimeoutError)
                {
                    this.TraceHelper.FunctionAborted(
                        this.Options.HubName,
                        context.FunctionName,
                        context.InstanceId,
                        $"An internal error occurred while attempting to execute this function. The execution will be aborted and retried. Details: {result.Exception}",
                        functionType: FunctionType.Orchestrator);

                    // This will abort the execution and cause the message to go back onto the queue for re-processing
                    throw new SessionAbortedException(
                        $"An internal error occurred while attempting to execute '{context.FunctionName}'.", result.Exception);
                }
            }

            if (!context.IsCompleted && !context.IsLongRunningTimer)
            {
                this.TraceHelper.FunctionAwaited(
                    context.HubName,
                    context.Name,
                    FunctionType.Orchestrator,
                    context.InstanceId,
                    context.IsReplaying);
            }

            if (context.IsCompleted &&
                context.PreserveUnprocessedEvents)
            {
                // Reschedule any unprocessed external events so that they can be picked up
                // in the next iteration.
                context.RescheduleBufferedExternalEvents();
            }

            await context.RunDeferredTasks();
        }

        /// <summary>
        /// This DTFx orchestration middleware (for entities) allows us to add context and set state
        /// to the entity shim orchestration before it starts executing the actual entity logic.
        /// </summary>
        /// <param name="dispatchContext">A property bag containing useful DTFx context.</param>
        /// <param name="next">The handler for running the next middleware in the pipeline.</param>
        private async Task EntityMiddleware(DispatchMiddlewareContext dispatchContext, Func<Task> next)
        {
            var entityShim = dispatchContext.GetProperty<TaskOrchestration>() as TaskEntityShim;
            if (entityShim == null)
            {
                // This is not an entity - skip.
                await next();
                return;
            }

            OrchestrationRuntimeState runtimeState = dispatchContext.GetProperty<OrchestrationRuntimeState>();
            DurableEntityContext entityContext = (DurableEntityContext)entityShim.Context;
            entityContext.InstanceId = runtimeState.OrchestrationInstance.InstanceId;
            entityContext.ExecutionId = runtimeState.OrchestrationInstance.ExecutionId;
            entityContext.History = runtimeState.Events;
            entityContext.RawInput = runtimeState.Input;

            Queue<RequestMessage> lockHolderMessages = null;

            try
            {
                // 1. First time through the history
                // we count events, add any under-lock op to the batch, and process lock releases
                foreach (HistoryEvent e in runtimeState.Events)
                {
                    switch (e.EventType)
                    {
                        case EventType.ExecutionStarted:
                            entityShim.Rehydrate(runtimeState.Input);
                            break;

                        case EventType.EventRaised:
                            EventRaisedEvent eventRaisedEvent = (EventRaisedEvent)e;

                            this.TraceHelper.DeliveringEntityMessage(
                                entityContext.InstanceId,
                                entityContext.ExecutionId,
                                e.EventId,
                                eventRaisedEvent.Name,
                                eventRaisedEvent.Input);

                            entityShim.NumberEventsToReceive++;

                            if (EntityMessageEventNames.IsRequestMessage(eventRaisedEvent.Name))
                            {
                                // we are receiving an operation request or a lock request
                                var requestMessage = this.MessageDataConverter.Deserialize<RequestMessage>(eventRaisedEvent.Input);

                                IEnumerable<RequestMessage> deliverNow;

                                if (requestMessage.ScheduledTime.HasValue)
                                {
                                    if ((requestMessage.ScheduledTime.Value - DateTime.UtcNow) > TimeSpan.FromMilliseconds(100))
                                    {
                                        // message was delivered too early. This can happen if the durability provider imposes
                                        // a limit on the delay. We handle this by rescheduling the message instead of processing it.
                                        deliverNow = Array.Empty<RequestMessage>();
                                        entityShim.AddMessageToBeRescheduled(requestMessage);
                                    }
                                    else
                                    {
                                        // the message is scheduled to be delivered immediately.
                                        // There are no FIFO guarantees for scheduled messages, so we skip the message sorter.
                                        deliverNow = new RequestMessage[] { requestMessage };
                                    }
                                }
                                else
                                {
                                    // run this through the message sorter to help with reordering and duplicate filtering
                                    deliverNow = entityContext.State.MessageSorter.ReceiveInOrder(requestMessage, this.MessageReorderWindow);
                                }

                                foreach (var message in deliverNow)
                                {
                                    if (entityContext.State.LockedBy == message.ParentInstanceId)
                                    {
                                        if (lockHolderMessages == null)
                                        {
                                            lockHolderMessages = new Queue<RequestMessage>();
                                        }

                                        lockHolderMessages.Enqueue(message);
                                    }
                                    else
                                    {
                                        entityContext.State.Enqueue(message);
                                    }
                                }
                            }
                            else if (EntityMessageEventNames.IsReleaseMessage(eventRaisedEvent.Name))
                            {
                                // we are receiving a lock release
                                var message = this.MessageDataConverter.Deserialize<ReleaseMessage>(eventRaisedEvent.Input);

                                if (entityContext.State.LockedBy == message.ParentInstanceId)
                                {
                                    this.TraceHelper.EntityLockReleased(
                                        entityContext.HubName,
                                        entityContext.Name,
                                        entityContext.InstanceId,
                                        message.ParentInstanceId,
                                        message.LockRequestId,
                                        isReplay: false);

                                    entityContext.State.LockedBy = null;
                                }
                            }
                            else
                            {
                                // this is a continue message.
                                // Resumes processing of previously queued operations, if any.
                                entityContext.State.Suspended = false;
                            }

                            break;
                    }
                }

                // lock holder messages go to the front of the queue
                if (lockHolderMessages != null)
                {
                    entityContext.State.PutBack(lockHolderMessages);
                }

                if (!entityContext.State.Suspended)
                {
                    // 2. We add as many requests from the queue to the batch as possible,
                    // stopping at lock requests or when the maximum batch size is reached
                    while (entityContext.State.MayDequeue())
                    {
                        if (entityShim.OperationBatch.Count == this.Options.MaxEntityOperationBatchSize)
                        {
                            // we have reached the maximum batch size already
                            // insert a delay after this batch to ensure write back
                            entityShim.ToBeContinuedWithDelay();
                            break;
                        }

                        var request = entityContext.State.Dequeue();

                        if (request.IsLockRequest)
                        {
                            entityShim.AddLockRequestToBatch(request);
                            break;
                        }
                        else
                        {
                            entityShim.AddOperationToBatch(request);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                entityContext.CaptureInternalError(e);
            }

            WrappedFunctionResult result;

            if (entityShim.OperationBatch.Count > 0 && !this.HostLifetimeService.OnStopping.IsCancellationRequested)
            {
                // 3a. Start the functions invocation pipeline (billing, logging, bindings, and timeout tracking).
                result = await FunctionExecutionHelper.ExecuteFunctionInOrchestrationMiddleware(
                    entityShim.GetFunctionInfo().Executor,
                    new TriggeredFunctionData
                    {
                        TriggerValue = entityShim.Context,
#pragma warning disable CS0618 // Approved for use by this extension
                        InvokeHandler = async userCodeInvoker =>
                            {
                                entityContext.ExecutorCalledBack = true;

                                entityShim.SetFunctionInvocationCallback(userCodeInvoker);

                                this.TraceHelper.FunctionStarting(
                                    entityContext.HubName,
                                    entityContext.Name,
                                    entityContext.InstanceId,
                                    this.GetIntputOutputTrace(runtimeState.Input),
                                    FunctionType.Entity,
                                    isReplay: false);

                                // 3. Run all the operations in the batch
                                if (entityContext.InternalError == null)
                                {
                                    try
                                    {
                                        await entityShim.ExecuteBatch(this.HostLifetimeService.OnStopping);
                                    }
                                    catch (Exception e)
                                    {
                                        entityContext.CaptureInternalError(e);
                                    }
                                }

                                // 4. Run the DTFx orchestration to persist the effects,
                                // send the outbox, and continue as new
                                await next();

                                // 5. If there were internal or application errors, trace them for DF
                                if (entityContext.ErrorsPresent(out var description))
                                {
                                    this.TraceHelper.FunctionFailed(
                                        entityContext.HubName,
                                        entityContext.Name,
                                        entityContext.InstanceId,
                                        description,
                                        functionType: FunctionType.Entity,
                                        isReplay: false);
                                }
                                else
                                {
                                    this.TraceHelper.FunctionCompleted(
                                        entityContext.HubName,
                                        entityContext.Name,
                                        entityContext.InstanceId,
                                        this.GetIntputOutputTrace(entityContext.State.EntityState),
                                        continuedAsNew: true,
                                        functionType: FunctionType.Entity,
                                        isReplay: false);
                                }

                                // 6. If there were internal or application errors, also rethrow them here so the functions host gets to see them
                                entityContext.ThrowInternalExceptionIfAny();
                                entityContext.ThrowApplicationExceptionsIfAny();
                            },
#pragma warning restore CS0618
                    },
                    entityShim,
                    entityContext,
                    this.HostLifetimeService.OnStopping);

                if (result.ExecutionStatus == WrappedFunctionResult.FunctionResultStatus.FunctionTimeoutError)
                {
                    await entityShim.TimeoutTask;
                }

                if (result.ExecutionStatus == WrappedFunctionResult.FunctionResultStatus.FunctionsRuntimeError
                    || result.ExecutionStatus == WrappedFunctionResult.FunctionResultStatus.FunctionsHostStoppingError)
                {
                    this.TraceHelper.FunctionAborted(
                      this.Options.HubName,
                      entityContext.FunctionName,
                      entityContext.InstanceId,
                      $"An internal error occurred while attempting to execute this function. The execution will be aborted and retried. Details: {result.Exception}",
                      functionType: FunctionType.Entity);

                    // This will abort the execution and cause the message to go back onto the queue for re-processing
                    throw new SessionAbortedException(
                        $"An internal error occurred while attempting to execute '{entityContext.FunctionName}'.",
                        result.Exception);
                }
            }
            else
            {
                // 3b. We do not need to call into user code because we are not going to run any operations.
                // In this case we can execute without involving the functions runtime.
                if (entityContext.InternalError == null)
                {
                    try
                    {
                        await entityShim.ExecuteBatch(this.HostLifetimeService.OnStopping);
                        await next();
                    }
                    catch (Exception e)
                    {
                        entityContext.CaptureInternalError(e);
                    }
                }
            }

            // If there were internal errors, throw a SessionAbortedException
            // here so DTFx can abort the batch and back off the work item
            entityContext.AbortOnInternalError();

            await entityContext.RunDeferredTasks();
        }

        internal string GetDefaultConnectionName()
        {
            return this.defaultDurabilityProvider.ConnectionName;
        }

        internal RegisteredFunctionInfo GetOrchestratorInfo(FunctionName orchestratorFunction)
        {
            this.knownOrchestrators.TryGetValue(orchestratorFunction, out var info);
            return info;
        }

        internal bool TryGetActivityInfo(FunctionName activityFunction, out RegisteredFunctionInfo info)
        {
            return this.knownActivities.TryGetValue(activityFunction, out info);
        }

        internal RegisteredFunctionInfo GetEntityInfo(FunctionName entityFunction)
        {
            this.knownEntities.TryGetValue(entityFunction, out var info);
            return info;
        }

        // This is temporary until script loading
        private static void ConfigureLoaderHooks()
        {
#if FUNCTIONS_V1
            AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;
#endif
        }

        private static Assembly ResolveAssembly(object sender, ResolveEventArgs args)
        {
            if (args.Name.StartsWith("DurableTask.Core"))
            {
                return typeof(TaskOrchestration).Assembly;
            }
            else if (args.Name.StartsWith("DurableTask.AzureStorage"))
            {
                return typeof(AzureStorageOrchestrationService).Assembly;
            }
            else if (args.Name.StartsWith("Microsoft.Azure.WebJobs.DurableTask"))
            {
                return typeof(DurableTaskExtension).Assembly;
            }

            return null;
        }

        /// <summary>
        /// Gets a <see cref="IDurableClient"/> using configuration from a <see cref="DurableClientAttribute"/> instance.
        /// </summary>
        /// <param name="attribute">The attribute containing the client configuration parameters.</param>
        /// <returns>Returns a <see cref="IDurableClient"/> instance. The returned instance may be a cached instance.</returns>
        protected internal virtual IDurableClient GetClient(DurableClientAttribute attribute)
        {
            DurableClient client = this.cachedClients.GetOrAdd(
                attribute,
                attr =>
                {
                    DurabilityProvider innerClient = this.durabilityProviderFactory.GetDurabilityProvider(attribute);
                    return new DurableClient(innerClient, this, this.HttpApiHandler, attr);
                });

            return client;
        }

        internal void RegisterOrchestrator(FunctionName orchestratorFunction, RegisteredFunctionInfo orchestratorInfo)
        {
            if (orchestratorInfo != null)
            {
                orchestratorInfo.IsDeregistered = false;
            }

            if (this.knownOrchestrators.TryAdd(orchestratorFunction, orchestratorInfo))
            {
                this.TraceHelper.ExtensionInformationalEvent(
                    this.Options.HubName,
                    instanceId: string.Empty,
                    functionName: orchestratorFunction.Name,
                    message: $"Registered orchestrator function named {orchestratorFunction}.",
                    writeToUserLogs: false);
            }
            else
            {
                this.knownOrchestrators[orchestratorFunction] = orchestratorInfo;
            }
        }

        internal void DeregisterOrchestrator(FunctionName orchestratorFunction)
        {
            RegisteredFunctionInfo existing;
            if (this.knownOrchestrators.TryGetValue(orchestratorFunction, out existing) && !existing.IsDeregistered)
            {
                existing.IsDeregistered = true;

                this.TraceHelper.ExtensionInformationalEvent(
                    this.Options.HubName,
                    instanceId: string.Empty,
                    functionName: orchestratorFunction.Name,
                    message: $"Deregistered orchestrator function named {orchestratorFunction}.",
                    writeToUserLogs: false);
            }
        }

        internal void RegisterActivity(FunctionName activityFunction, ITriggeredFunctionExecutor executor)
        {
            if (this.knownActivities.TryGetValue(activityFunction, out RegisteredFunctionInfo existing))
            {
                existing.Executor = executor;
            }
            else
            {
                var info = new RegisteredFunctionInfo(executor, isOutOfProc: false);
                this.knownActivities[activityFunction] = info;

                this.TraceHelper.ExtensionInformationalEvent(
                    this.Options.HubName,
                    instanceId: string.Empty,
                    functionName: activityFunction.Name,
                    message: $"Registered activity function named {activityFunction}.",
                    writeToUserLogs: false);
            }
        }

        internal void DeregisterActivity(FunctionName activityFunction)
        {
            RegisteredFunctionInfo info;
            if (this.knownActivities.TryGetValue(activityFunction, out info) && !info.IsDeregistered)
            {
                info.IsDeregistered = true;

                this.TraceHelper.ExtensionInformationalEvent(
                    this.Options.HubName,
                    instanceId: string.Empty,
                    functionName: activityFunction.Name,
                    message: $"Deregistered activity function named {activityFunction}.",
                    writeToUserLogs: false);
            }
        }

        internal void RegisterEntity(FunctionName entityFunction, RegisteredFunctionInfo entityInfo)
        {
            if (entityInfo != null)
            {
                entityInfo.IsDeregistered = false;
            }

            if (this.knownEntities.TryAdd(entityFunction, entityInfo))
            {
                this.TraceHelper.ExtensionInformationalEvent(
                    this.Options.HubName,
                    instanceId: string.Empty,
                    functionName: entityFunction.Name,
                    message: $"Registered entity function named {entityFunction}.",
                    writeToUserLogs: false);
            }
            else
            {
                this.knownEntities[entityFunction] = entityInfo;
            }
        }

        internal void DeregisterEntity(FunctionName entityFunction)
        {
            RegisteredFunctionInfo existing;
            if (this.knownEntities.TryGetValue(entityFunction, out existing) && !existing.IsDeregistered)
            {
                existing.IsDeregistered = true;

                this.TraceHelper.ExtensionInformationalEvent(
                    this.Options.HubName,
                    instanceId: string.Empty,
                    functionName: entityFunction.Name,
                    message: $"Deregistered entity function named {entityFunction}.",
                    writeToUserLogs: false);
            }
        }

        internal void ThrowIfFunctionDoesNotExist(string name, FunctionType functionType)
        {
            if (IsDurableHttpTask(name))
            {
                return;
            }

            var functionName = new FunctionName(name);

            if (functionType == FunctionType.Activity && !this.knownActivities.ContainsKey(functionName))
            {
                throw new ArgumentException(this.GetInvalidActivityFunctionMessage(name));
            }
            else if (functionType == FunctionType.Orchestrator && !this.knownOrchestrators.ContainsKey(functionName))
            {
                throw new ArgumentException(this.GetInvalidOrchestratorFunctionMessage(name));
            }
            else if (functionType == FunctionType.Entity && !this.knownEntities.ContainsKey(functionName))
            {
                throw new ArgumentException(this.GetInvalidEntityFunctionMessage(name));
            }
        }

        private static bool IsDurableHttpTask(string functionName)
        {
            return string.Equals(functionName, HttpOptions.HttpTaskActivityReservedName);
        }

        internal string GetInvalidActivityFunctionMessage(string name)
        {
            string message = $"The function '{name}' doesn't exist, is disabled, or is not an activity function. Additional info: ";
            if (this.knownActivities.Keys.Count > 0)
            {
                message += $"The following are the known activity functions: '{string.Join("', '", this.knownActivities.Keys)}'.";
            }
            else
            {
                message += "No activity functions are currently registered!";
            }

            return message;
        }

        internal string GetInvalidOrchestratorFunctionMessage(string name)
        {
            string message = $"The function '{name}' doesn't exist, is disabled, or is not an orchestrator function. Additional info: ";
            if (this.knownOrchestrators.Keys.Count > 0)
            {
                message += $"The following are the known orchestrator functions: '{string.Join("', '", this.knownOrchestrators.Keys)}'.";
            }
            else
            {
                message += "No orchestrator functions are currently registered!";
            }

            return message;
        }

        internal string GetInvalidEntityFunctionMessage(string name)
        {
            string message = $"The function '{name}' doesn't exist, is disabled, or is not an entity function. Additional info: ";
            if (this.knownOrchestrators.Keys.Count > 0)
            {
                message += $"The following are the known entity functions: '{string.Join("', '", this.knownEntities.Keys)}'.";
            }
            else
            {
                message += "No entity functions are currently registered!";
            }

            return message;
        }

        internal async Task<bool> StartTaskHubWorkerIfNotStartedAsync()
        {
            if (!this.isTaskHubWorkerStarted)
            {
                using (await this.taskHubLock.AcquireAsync())
                {
                    if (!this.isTaskHubWorkerStarted)
                    {
                        this.TraceHelper.ExtensionInformationalEvent(
                            this.Options.HubName,
                            instanceId: string.Empty,
                            functionName: string.Empty,
                            message: "Starting task hub worker",
                            writeToUserLogs: true);

                        Stopwatch sw = Stopwatch.StartNew();
                        await this.defaultDurabilityProvider.CreateIfNotExistsAsync();
                        await this.taskHubWorker.StartAsync();

                        this.TraceHelper.ExtensionInformationalEvent(
                            this.Options.HubName,
                            instanceId: string.Empty,
                            functionName: string.Empty,
                            message: $"Task hub worker started. Latency: {sw.Elapsed}",
                            writeToUserLogs: true);

                        // Enable flowing exception information from activities
                        // to the parent orchestration code.
                        if (this.taskHubWorker.TaskActivityDispatcher != null)
                        {
                            this.taskHubWorker.TaskActivityDispatcher.IncludeDetails = true;
                        }

                        if (this.taskHubWorker.TaskOrchestrationDispatcher != null)
                        {
                            this.taskHubWorker.TaskOrchestrationDispatcher.IncludeDetails = true;
                        }

                        this.isTaskHubWorkerStarted = true;
                        return true;
                    }
                }
            }

            return false;
        }

        internal async Task<bool> StopTaskHubWorkerIfIdleAsync()
        {
            using (await this.taskHubLock.AcquireAsync())
            {
                bool HasActiveListeners(RegisteredFunctionInfo info)
                    => info?.HasActiveListener ?? false; // info can be null if function is disabled via attribute

                // Wait to shut down the task hub worker until all function listeners have been shut down.
                if (this.isTaskHubWorkerStarted &&
                    !this.knownOrchestrators.Values.Any(HasActiveListeners) &&
                    !this.knownActivities.Values.Any(HasActiveListeners) &&
                    !this.knownEntities.Values.Any(HasActiveListeners))
                {
                    bool isGracefulStop = this.Options.UseGracefulShutdown;

                    this.TraceHelper.ExtensionInformationalEvent(
                        this.Options.HubName,
                        instanceId: string.Empty,
                        functionName: string.Empty,
                        message: $"Stopping task hub worker. IsGracefulStop: {isGracefulStop}",
                        writeToUserLogs: true);

                    Stopwatch sw = Stopwatch.StartNew();
                    await this.taskHubWorker.StopAsync(isForced: !isGracefulStop);
                    this.isTaskHubWorkerStarted = false;

                    this.TraceHelper.ExtensionInformationalEvent(
                        this.Options.HubName,
                        instanceId: string.Empty,
                        functionName: string.Empty,
                        message: $"Task hub worker stopped. IsGracefulStop: {isGracefulStop}. Latency: {sw.Elapsed}",
                        writeToUserLogs: true);

                    return true;
                }
            }

            return false;
        }

        internal string GetIntputOutputTrace(string rawInputOutputData)
        {
            if (this.Options.Tracing.TraceInputsAndOutputs)
            {
                return rawInputOutputData;
            }
            else if (rawInputOutputData == null)
            {
                return "(null)";
            }
            else
            {
                // Azure Storage uses UTF-32 encoding for string payloads
                return "(" + Encoding.UTF32.GetByteCount(rawInputOutputData) + " bytes)";
            }
        }

        internal string GetExceptionTrace(string rawExceptionData)
        {
            if (rawExceptionData == null)
            {
                return "(null)";
            }
            else
            {
                return rawExceptionData;
            }
        }

        /// <inheritdoc/>
        Task<HttpResponseMessage> IAsyncConverter<HttpRequestMessage, HttpResponseMessage>.ConvertAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return this.HttpApiHandler.HandleRequestAsync(request);
        }

        internal static string ValidatePayloadSize(string payload)
        {
            // The payload gets written to Azure Table Storage and to Azure Queues, which have
            // strict storage limitations (64 KB). Until we support large messages, we need to block them.
            // https://github.com/Azure/azure-functions-durable-extension/issues/79
            // We limit to 60 KB (UTF-32 encoding) to leave room for metadata.
            if (Encoding.UTF32.GetByteCount(payload) > 60 * 1024)
            {
                throw new ArgumentException("The size of the JSON-serialized payload must not exceed 60 KB of UTF-32 encoded text.");
            }

            return payload;
        }

#if !FUNCTIONS_V1
        /// <summary>
        /// Tags the current Activity with metadata: DurableFunctionsType, DurableFunctionsInstanceId, DurableFunctionsRuntimeStatus.
        /// This metadata will show up in Application Insights, if enabled.
        /// </summary>
        internal static void TagActivityWithOrchestrationStatus(OrchestrationRuntimeStatus status, string instanceId, bool isEntity = false)
        {
            // Adding "Tags" to activity allows using Application Insights to query current state of orchestrations
            Activity activity = Activity.Current;
            string functionsType = isEntity ? "Entity" : "Orchestrator";

            // The activity may be null when running unit tests, but should be non-null otherwise
            if (activity != null)
            {
                string statusStr = Enum.GetName(status.GetType(), status);
                activity.AddTag("DurableFunctionsType", functionsType);
                activity.AddTag("DurableFunctionsInstanceId", instanceId);
                activity.AddTag("DurableFunctionsRuntimeStatus", statusStr);
            }
        }

        internal IScaleMonitor GetScaleMonitor(string functionId, FunctionName functionName, string connectionName)
        {
            if (this.defaultDurabilityProvider.TryGetScaleMonitor(
                    functionId,
                    functionName.Name,
                    this.Options.HubName,
                    connectionName,
                    out IScaleMonitor scaleMonitor))
            {
                return scaleMonitor;
            }
            else
            {
                // the durability provider does not support runtime scaling.
                // Create an empty scale monitor to avoid exceptions (unless runtime scaling is actually turned on).
                return new NoOpScaleMonitor($"{functionId}-DurableTaskTrigger-{this.Options.HubName}".ToLower());
            }
        }

        /// <summary>
        /// A placeholder scale monitor, can be used by durability providers that do not support runtime scaling.
        /// This is required to allow operation of those providers even if runtime scaling is turned off
        /// see discussion https://github.com/Azure/azure-functions-durable-extension/pull/1009/files#r341767018.
        /// </summary>
        private sealed class NoOpScaleMonitor : IScaleMonitor
        {
            /// <summary>
            /// Construct a placeholder scale monitor.
            /// </summary>
            /// <param name="name">A descriptive name.</param>
            public NoOpScaleMonitor(string name)
            {
                this.Descriptor = new ScaleMonitorDescriptor(name);
            }

            /// <summary>
            /// A descriptive name.
            /// </summary>
            public ScaleMonitorDescriptor Descriptor { get; private set; }

            /// <inheritdoc/>
            Task<ScaleMetrics> IScaleMonitor.GetMetricsAsync()
            {
                throw new InvalidOperationException("The current DurableTask backend configuration does not support runtime scaling");
            }

            /// <inheritdoc/>
            ScaleStatus IScaleMonitor.GetScaleStatus(ScaleStatusContext context)
            {
                throw new InvalidOperationException("The current DurableTask backend configuration does not support runtime scaling");
            }
        }
#endif
    }
}

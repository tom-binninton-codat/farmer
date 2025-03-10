module Program

open Expecto
open System
open Farmer

let hasEnv a b =
    Environment.GetEnvironmentVariable a = b

let notEnv a b =
    Environment.GetEnvironmentVariable a <> b

[<Tests>]
let allTests =
    testSequencedGroup ""
    <| testList
        "All Tests"
        [
            testList
                "Builders"
                [
                    AppGateway.tests
                    AppInsights.tests
                    AppInsightsAvailability.tests
                    if notEnv "BUILD_REASON" "PullRequest" then
                        AzCli.tests
                    AzureFirewall.tests
                    Bastion.tests
                    BingSearch.tests
                    Cdn.tests
                    CognitiveServices.tests
                    CommunicationServices.tests
                    ContainerApps.tests
                    ContainerGroup.tests
                    ContainerRegistry.tests
                    ContainerService.tests
                    Cosmos.tests
                    Databricks.tests
                    DeploymentScript.tests
                    DiagnosticSettings.tests
                    Dns.tests
                    EventGrid.tests
                    EventHub.tests
                    ExpressRoute.tests
                    Functions.tests
                    IotHub.tests
                    JsonRegression.tests
                    KeyVault.tests
                    Network.tests
                    LoadBalancer.tests
                    LogAnalytics.tests
                    LogicApps.tests
                    Maps.tests
                    NetworkSecurityGroup.tests
                    OperationsManagement.tests
                    PostgreSQL.tests
                    PrivateLink.tests
                    ResourceGroup.tests
                    ServiceBus.tests
                    SignalR.tests
                    Sql.tests
                    StaticWebApp.tests
                    Storage.tests
                    TrafficManager.tests
                    Types.tests
                    VirtualHub.tests
                    VirtualMachine.tests
                    VirtualNetworkGateway.tests
                    VirtualWan.tests
                    WebApp.tests
                    Dashboards.tests
                    Alerts.tests
                    ServicePlan.tests
                    Redis.tests
                ]
            testList
                "Control"
                [
                    if
                        (hasEnv "TF_BUILD" "True" && notEnv "BUILD_REASON" "PullRequest")
                        || hasEnv "FARMER_E2E" "True"
                    then
                        AzCli.endToEndTests
                    Common.tests
                    Identity.tests
                    Template.tests
                ]
        ]

[<EntryPoint>]
let main _ =
    printfn "Running tests!"

    runTests
        { defaultConfig with
            verbosity = Logging.Info
        }
        allTests

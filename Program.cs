﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License. See License.txt in the project root for license information.

using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager.Resources.Models;
using Azure.ResourceManager.Samples.Common;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager;
using Azure.ResourceManager.Network;
using Azure.ResourceManager.Network.Models;

namespace ManageVpnGatewayVNet2VNetConnection
{
    public class Program
    {
        private static ResourceIdentifier? _resourceGroupId = null;

        /**
         * Azure Network sample for managing virtual network gateway.
         *  - Create 2 virtual networks with subnets and 2 virtual network gateways corresponding to each network
         *  - Create VPN VNet-to-VNet connection
         *  - Troubleshoot the connection
         *    - Create network watcher in the same region as virtual network gateway
         *    - Create storage account to store troubleshooting information
         *    - Run troubleshooting for the connection - result will be 'UnHealthy' as need to create symmetrical connection from second gateway to the first
         *  - Create virtual network connection from second gateway to the first and run troubleshooting. Result will be 'Healthy'.
         *  - List VPN Gateway connections for the first gateway
         *  - Create 2 virtual machines, each one in its network and verify connectivity between them
         */
        public static async Task RunSample(ArmClient client)
        {
            try
            {
                // Get default subscription
                SubscriptionResource subscription = await client.GetDefaultSubscriptionAsync();

                // Create a resource group in the EastUS region
                string rgName = Utilities.CreateRandomName("NetworkSampleRG");
                Utilities.Log($"Creating resource group...");
                ArmOperation<ResourceGroupResource> rgLro = await subscription.GetResourceGroups().CreateOrUpdateAsync(WaitUntil.Completed, rgName, new ResourceGroupData(AzureLocation.EastUS));
                ResourceGroupResource resourceGroup = rgLro.Value;
                _resourceGroupId = resourceGroup.Id;
                Utilities.Log("Created a resource group with name: " + resourceGroup.Data.Name);

                //============================================================
                // Create virtual network
                Utilities.Log("Creating virtual network...");
                INetwork network1 = azure.Networks.Define(vnetName)
                    .WithRegion(region)
                    .WithNewResourceGroup(rgName)
                    .WithAddressSpace("10.11.0.0/16")
                    .WithSubnet("GatewaySubnet", "10.11.255.0/27")
                    .WithSubnet("Subnet1", "10.11.0.0/24")
                    .Create();
                Utilities.Log("Created network");
                // Print the virtual network
                Utilities.PrintVirtualNetwork(network1);

                //============================================================
                // Create virtual network gateway
                Utilities.Log("Creating virtual network gateway...");
                IVirtualNetworkGateway vngw1 = azure.VirtualNetworkGateways.Define(vpnGatewayName)
                    .WithRegion(region)
                    .WithNewResourceGroup(rgName)
                    .WithExistingNetwork(network1)
                    .WithRouteBasedVpn()
                    .WithSku(VirtualNetworkGatewaySkuName.VpnGw1)
                    .Create();
                Utilities.Log("Created virtual network gateway");

                //============================================================
                // Create second virtual network
                Utilities.Log("Creating virtual network...");
                INetwork network2 = azure.Networks.Define(vnet2Name)
                    .WithRegion(region)
                    .WithNewResourceGroup(rgName)
                    .WithAddressSpace("10.41.0.0/16")
                    .WithSubnet("GatewaySubnet", "10.41.255.0/27")
                    .WithSubnet("Subnet2", "10.41.0.0/24")
                    .Create();
                Utilities.Log("Created virtual network");

                //============================================================
                // Create second virtual network gateway
                Utilities.Log("Creating second virtual network gateway...");
                IVirtualNetworkGateway vngw2 = azure.VirtualNetworkGateways.Define(vpnGateway2Name)
                    .WithRegion(region)
                    .WithNewResourceGroup(rgName)
                    .WithExistingNetwork(network2)
                    .WithRouteBasedVpn()
                    .WithSku(VirtualNetworkGatewaySkuName.VpnGw1)
                    .Create();
                Utilities.Log("Created second virtual network gateway");

                //============================================================
                // Create virtual network gateway connection
                Utilities.Log("Creating virtual network gateway connection...");
                IVirtualNetworkGatewayConnection connection = vngw1.Connections
                    .Define(connectionName)
                    .WithVNetToVNet()
                    .WithSecondVirtualNetworkGateway(vngw2)
                    .WithSharedKey("MySecretKey")
                    .Create();
                Utilities.Log("Created virtual network gateway connection");

                //============================================================
                // Troubleshoot the connection

                // create Network Watcher
                INetworkWatcher nw = azure.NetworkWatchers.Define(nwName)
                        .WithRegion(region)
                        .WithExistingResourceGroup(rgName)
                        .Create();
                // Create storage account to store troubleshooting information
                IStorageAccount storageAccount = azure.StorageAccounts.Define("sa" + SdkContext.RandomResourceName("", 8))
                        .WithRegion(region)
                        .WithExistingResourceGroup(rgName)
                        .Create();
                // Create storage container to store troubleshooting results
                string accountKey = storageAccount.GetKeys()[0].Value;
                string connectionString = string.Format("DefaultEndpointsProtocol=https;AccountName={0};AccountKey={1}", storageAccount.Name, accountKey);
                Utilities.CreateContainer(connectionString, containerName);

                // Run troubleshooting for the connection - result will be 'UnHealthy' as need to create symmetrical connection from second gateway to the first
                ITroubleshooting troubleshooting = nw.Troubleshoot()
                        .WithTargetResourceId(connection.Id)
                        .WithStorageAccount(storageAccount.Id)
                        .WithStoragePath(storageAccount.EndPoints.Primary.Blob + containerName)
                        .Execute();
                Utilities.Log("Troubleshooting status is: " + troubleshooting.Code);

                //============================================================
                //  Create virtual network connection from second gateway to the first and run troubleshooting. Result will be 'Healthy'.
                vngw2.Connections
                        .Define(connection2Name)
                        .WithVNetToVNet()
                        .WithSecondVirtualNetworkGateway(vngw1)
                        .WithSharedKey("MySecretKey")
                        .Create();
                // Delay before running troubleshooting to wait for connection settings to propagate
                SdkContext.DelayProvider.Delay(250000);
                troubleshooting = nw.Troubleshoot()
                        .WithTargetResourceId(connection.Id)
                        .WithStorageAccount(storageAccount.Id)
                        .WithStoragePath(storageAccount.EndPoints.Primary.Blob + containerName)
                        .Execute();
                Utilities.Log("Troubleshooting status is: " + troubleshooting.Code);

                //============================================================
                // List VPN Gateway connections for particular gateway
                var connections = vngw1.ListConnections();
                foreach (var conn in connections)
                {
                    Utilities.Print(conn);
                }

                //============================================================
                // Create 2 virtual machines, each one in its network and verify connectivity between them
                List<ICreatable<IVirtualMachine>> vmDefinitions = new List<ICreatable<IVirtualMachine>>();

                vmDefinitions.Add(azure.VirtualMachines.Define(vm1Name)
                        .WithRegion(region)
                        .WithExistingResourceGroup(rgName)
                        .WithExistingPrimaryNetwork(network1)
                        .WithSubnet("Subnet1")
                        .WithPrimaryPrivateIPAddressDynamic()
                        .WithoutPrimaryPublicIPAddress()
                        .WithPopularLinuxImage(KnownLinuxVirtualMachineImage.UbuntuServer16_04_Lts)
                        .WithRootUsername(rootname)
                        .WithRootPassword(password)
                        // Extension currently needed for network watcher support
                        .DefineNewExtension("networkWatcher")
                            .WithPublisher("Microsoft.Azure.NetworkWatcher")
                            .WithType("NetworkWatcherAgentLinux")
                            .WithVersion("1.4")
                            .Attach());
                vmDefinitions.Add(azure.VirtualMachines.Define(vm2Name)
                        .WithRegion(region)
                        .WithExistingResourceGroup(rgName)
                        .WithExistingPrimaryNetwork(network2)
                        .WithSubnet("Subnet2")
                        .WithPrimaryPrivateIPAddressDynamic()
                        .WithoutPrimaryPublicIPAddress()
                        .WithPopularLinuxImage(KnownLinuxVirtualMachineImage.UbuntuServer16_04_Lts)
                        .WithRootUsername(rootname)
                        .WithRootPassword(password)
                        // Extension currently needed for network watcher support
                        .DefineNewExtension("networkWatcher")
                            .WithPublisher("Microsoft.Azure.NetworkWatcher")
                            .WithType("NetworkWatcherAgentLinux")
                            .WithVersion("1.4")
                            .Attach());
                ICreatedResources<IVirtualMachine> createdVMs = azure.VirtualMachines.Create(vmDefinitions);
                IVirtualMachine vm1 = createdVMs.FirstOrDefault(vm => vm.Key == vmDefinitions[0].Key);
                IVirtualMachine vm2 = createdVMs.FirstOrDefault(vm => vm.Key == vmDefinitions[1].Key);

                IConnectivityCheck connectivity = nw.CheckConnectivity()
                        .ToDestinationResourceId(vm2.Id)
                        .ToDestinationPort(22)
                        .FromSourceVirtualMachine(vm1.Id)
                        .Execute();
                Utilities.Log("Connectivity status: " + connectivity.ConnectionStatus);
            }
            finally
            {
                try
                {
                    if (_resourceGroupId is not null)
                    {
                        Utilities.Log($"Deleting Resource Group...");
                        await client.GetResourceGroupResource(_resourceGroupId).DeleteAsync(WaitUntil.Completed);
                        Utilities.Log($"Deleted Resource Group: {_resourceGroupId.Name}");
                    }
                }
                catch (NullReferenceException)
                {
                    Utilities.Log("Did not create any resources in Azure. No clean up is necessary");
                }
                catch (Exception ex)
                {
                    Utilities.Log(ex);
                }
            }
        }

        public static async Task Main(string[] args)
        {

            try
            {
                //=================================================================
                // Authenticate
                var clientId = Environment.GetEnvironmentVariable("CLIENT_ID");
                var clientSecret = Environment.GetEnvironmentVariable("CLIENT_SECRET");
                var tenantId = Environment.GetEnvironmentVariable("TENANT_ID");
                var subscription = Environment.GetEnvironmentVariable("SUBSCRIPTION_ID");
                ClientSecretCredential credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
                ArmClient client = new ArmClient(credential, subscription);

                await RunSample(client);
            }
            catch (Exception ex)
            {
                Utilities.Log(ex);
            }
        }
    }
}
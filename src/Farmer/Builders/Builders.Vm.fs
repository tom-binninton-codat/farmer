[<AutoOpen>]
module Farmer.Builders.VirtualMachine

open Farmer
open Farmer.PublicIpAddress
open Farmer.PrivateIpAddress
open Farmer.Vm
open Farmer.Helpers
open Farmer.Arm.Compute
open Farmer.Arm.Network
open Farmer.Arm.Storage
open System
open Farmer.Identity

let makeName (vmName:ResourceName) elementType = ResourceName $"{vmName.Value}-%s{elementType}"

type VmConfig =
    { Name : ResourceName
      DiagnosticsStorageAccount : ResourceRef<VmConfig> option

      Priority: Priority option

      Username : string option
      PasswordParameter: string option
      Image : ImageDefinition
      Size : VMSize
      OsDisk : DiskInfo
      DataDisks : DiskInfo list

      CustomScript : string option
      CustomScriptFiles : Uri list

      DomainNamePrefix : string option

      CustomData : string option
      DisablePasswordAuthentication : bool option
      SshPathAndPublicKeys : (string * string ) list option
      AadSshLogin : FeatureFlag

      VNet : ResourceRef<VmConfig>
      AddressPrefix : string
      SubnetPrefix : string
      Subnet : AutoGeneratedResource<VmConfig>
      PublicIp: ResourceRef<VmConfig> option
      IpAllocation: PublicIpAddress.AllocationMethod option
      PrivateIpAllocation: PrivateIpAddress.AllocationMethod option
      Identity : Identity.ManagedIdentity
      NetworkSecurityGroup: LinkedResource option

      Tags: Map<string,string> }

    member internal this.DeriveResourceName (resourceType:ResourceType) elementName = resourceType.resourceId (makeName this.Name elementName)
    member this.NicName = this.DeriveResourceName networkInterfaces "nic"
    member this.PublicIpId = this.PublicIp |> Option.map (fun ref -> ref.resourceId this) //(this.DeriveResourceName publicIPAddresses "ip")
    member this.PublicIpAddress = this.PublicIpId |> Option.map (fun ipid -> ArmExpression.create($"reference({ipid.ArmExpression.Value}).ipAddress"))
    member this.Hostname = this.PublicIpId |> Option.map (fun ip -> ip.ArmExpression.Map(sprintf "%s.dnsSettings.fqdn"))
    member this.SystemIdentity = SystemIdentity this.ResourceId
    member this.ResourceId = virtualMachines.resourceId this.Name
    member this.PasswordParameterArm = this.PasswordParameter |> Option.defaultValue $"password-for-{this.Name.Value}"

    interface IBuilder with
        member this.ResourceId = this.ResourceId
        member this.BuildResources location = [
            // VM itself
            { Name = this.Name
              Location = location
              StorageAccount =
                this.DiagnosticsStorageAccount
                |> Option.map(fun r -> r.resourceId(this).Name)
              NetworkInterfaceName = this.NicName.Name
              Size = this.Size
              Priority = this.Priority |> Option.defaultValue Regular
              Credentials =
                match this.Username with
                | Some username ->
                    {| Username = username
                       Password = SecureParameter this.PasswordParameterArm |}
                | None ->
                    raiseFarmer $"You must specify a username for virtual machine {this.Name.Value}"
              CustomData = this.CustomData
              DisablePasswordAuthentication = this.DisablePasswordAuthentication
              PublicKeys =
                if this.DisablePasswordAuthentication.IsSome && this.DisablePasswordAuthentication.Value && this.SshPathAndPublicKeys.IsNone then
                  raiseFarmer $"You must include at least one ssh key when Password Authentication is disabled"
                else
                  (this.SshPathAndPublicKeys)
              Identity = this.Identity
              Image = this.Image
              OsDisk = this.OsDisk
              DataDisks = this.DataDisks
              Tags = this.Tags }

            let vnetName = this.VNet.resourceId(this).Name
            let subnetName = this.Subnet.resourceId this
            let nsgId = this.NetworkSecurityGroup |> Option.map(fun nsg -> nsg.ResourceId)

            // NIC
            { Name = this.NicName.Name
              Location = location
              IpConfigs = [
                {| SubnetName = subnetName.Name
                   PublicIpAddress =
                        this.PublicIp
                        |> Option.map (fun x -> x.toLinkedResource this) |} ]
              VirtualNetwork = vnetName
              PrivateIpAllocation = this.PrivateIpAllocation
              NetworkSecurityGroup = nsgId
              Tags = this.Tags }

            // VNET
            match this.VNet with
            | DeployableResource this vnet ->
                { Name = vnetName
                  Location = location
                  AddressSpacePrefixes = [ this.AddressPrefix ]
                  Subnets = [
                      { Name = subnetName.Name
                        Prefix = this.SubnetPrefix
                        VirtualNetwork = Some (Managed vnet)
                        NetworkSecurityGroup = nsgId |> Option.map(fun x -> Managed x)
                        Delegations = []
                        ServiceEndpoints = []
                        AssociatedServiceEndpointPolicies = []
                        PrivateEndpointNetworkPolicies = None
                        PrivateLinkServiceNetworkPolicies = None
                        RouteTable = None }
                  ]
                  Tags = this.Tags
                }
            | _ ->
                ()

            // IP Address
            match this.PublicIpId with
            | Some resId ->
              { Name = resId.Name
                Location = location
                AllocationMethod =
                    match this.IpAllocation with
                    | Some x -> x
                    | None -> PublicIpAddress.AllocationMethod.Dynamic
                Sku = PublicIpAddress.Sku.Basic
                DomainNameLabel = this.DomainNamePrefix
                Tags = this.Tags }
            | _ -> ()

            // Storage account - optional
            match this.DiagnosticsStorageAccount with
            | Some (DeployableResource this resourceId) ->
                { Name = Storage.StorageAccountName.Create(resourceId.Name).OkValue
                  Location = location
                  Dependencies = []
                  Sku = Storage.Sku.Standard_LRS
                  NetworkAcls = None
                  StaticWebsite = None
                  EnableHierarchicalNamespace = None
                  MinTlsVersion = None
                  Tags = this.Tags }
            | Some _
            | None ->
                ()

            // Custom Script - optional
            match this.CustomScript, this.CustomScriptFiles with
            | Some script, files ->
                { Name = this.Name.Map(sprintf "%s-custom-script")
                  Location = location
                  VirtualMachine = this.Name
                  OS = this.Image.OS
                  ScriptContents = script
                  FileUris = files
                  Tags = this.Tags }
            | None, [] ->
                ()
            | None, _ ->
                raiseFarmer $"You have supplied custom script files {this.CustomScriptFiles} but no script. Custom script files are not automatically executed; you must provide an inline script which acts as a bootstrapper using the custom_script keyword."

            /// Azure AD SSH login extension
            match this.AadSshLogin with
            | FeatureFlag.Enabled when this.Image.OS = Linux && this.Identity.SystemAssigned = Disabled ->
                raiseFarmer "AAD SSH login requires that system assigned identity be enabled on the virtual machine."
            | FeatureFlag.Enabled when this.Image.OS = Windows ->
                raiseFarmer "AAD SSH login is only supported for Linux Virtual Machines"
            | FeatureFlag.Enabled ->
                { AadSshLoginExtension.Location = location
                  VirtualMachine = this.Name
                  Tags = this.Tags }
            | FeatureFlag.Disabled -> ()
        ]

type VirtualMachineBuilder() =
    let automaticPublicIp = Derived (fun (config:VmConfig) -> config.DeriveResourceName publicIPAddresses "ip") |> AutoGeneratedResource |> Some
    member _.Yield _ =
        { Name = ResourceName.Empty
          DiagnosticsStorageAccount = None
          Priority = None
          Size = Basic_A0
          Username = None
          PasswordParameter = None
          Image = WindowsServer_2012Datacenter
          DataDisks = []
          Identity = ManagedIdentity.Empty
          CustomScript = None
          CustomScriptFiles = []
          DomainNamePrefix = None
          CustomData = None
          DisablePasswordAuthentication = None
          SshPathAndPublicKeys = None
          AadSshLogin = FeatureFlag.Disabled
          OsDisk = { Size = 128; DiskType = Standard_LRS }
          AddressPrefix = "10.0.0.0/16"
          SubnetPrefix = "10.0.0.0/24"
          VNet = derived (fun config -> config.DeriveResourceName virtualNetworks "vnet")
          Subnet = Derived(fun config -> config.DeriveResourceName subnets "subnet")
          PublicIp = automaticPublicIp
          IpAllocation = None
          PrivateIpAllocation = None
          NetworkSecurityGroup = None
          Tags = Map.empty }

    member _.Run (state:VmConfig) =
        { state with
            DataDisks =
                match state.DataDisks with
                | [] -> [ { Size = 1024; DiskType = DiskType.Standard_LRS } ]
                | other -> other
        }

    /// Sets the name of the VM.
    [<CustomOperation "name">]
    member _.Name(state:VmConfig, name) = { state with Name = name }
    member this.Name(state:VmConfig, name) = this.Name(state, ResourceName name)
    /// Turns on diagnostics support using an automatically created storage account.
    [<CustomOperation "diagnostics_support">]
    member _.StorageAccountName(state:VmConfig) =
        let storageResourceRef = derived (fun config ->
            let name = config.Name.Map (sprintf "%sstorage") |> sanitiseStorage |> ResourceName
            storageAccounts.resourceId name)

        { state with DiagnosticsStorageAccount = Some storageResourceRef }
    /// Turns on diagnostics support using an externally managed storage account.
    [<CustomOperation "diagnostics_support_external">]
    member _.StorageAccountNameExternal(state:VmConfig, name) = { state with DiagnosticsStorageAccount = Some (LinkedResource name) }
    /// Sets the size of the VM.
    [<CustomOperation "vm_size">]
    member _.VmSize(state:VmConfig, size) = { state with Size = size }
    /// Sets the admin username of the VM (note: the password is supplied as a securestring parameter to the generated ARM template).
    [<CustomOperation "username">]
    member _.Username(state:VmConfig, username) = { state with Username = Some username }
    /// Sets the name of the template parameter which will contain the admin password for this VM. defaults to "password-for-<vmName>"
    [<CustomOperation "password_parameter">]
    member _.PasswordParameter(state:VmConfig, parameterName) = { state with PasswordParameter = Some parameterName }
    /// Sets the operating system of the VM. A set of samples is provided in the `CommonImages` module.
    [<CustomOperation "operating_system">]
    member _.ConfigureOs(state:VmConfig, image) =
        { state with Image = image }
    member _.ConfigureOs(state:VmConfig, (os, offer, publisher, sku)) =
        { state with Image = { OS = os; Offer = Offer offer; Publisher = Publisher publisher; Sku = ImageSku sku } }
    /// Sets the size and type of the OS disk for the VM.
    [<CustomOperation "os_disk">]
    member _.OsDisk(state:VmConfig, size, diskType) =
        { state with OsDisk = { Size = size; DiskType = diskType } }
    /// Adds a data disk to the VM with a specific size and type.
    [<CustomOperation "add_disk">]
    member _.AddDisk(state:VmConfig, size, diskType) = { state with DataDisks = { Size = size; DiskType = diskType } :: state.DataDisks }
    /// Sets priority of VMm. Overrides spot_instance.
    [<CustomOperation "priority">]
    member _.Priority(state:VmConfig, priority) =
        match state.Priority with
        | Some priority -> raiseFarmer $"Priority is already set to {priority}. Only one priority or spot_instance setting per VM is allowed"
        | None -> { state with Priority = Some priority }
    /// Makes VM a spot instance. Overrides priority.
    [<CustomOperation "spot_instance">]
    member _.Spot(state:VmConfig, (evictionPolicy, maxPrice)) : VmConfig =
        match state.Priority with
        | Some priority -> raiseFarmer $"Priority is already set to {priority}. Only one priority or spot_instance setting per VM is allowed"
        | None -> { state with Priority = (evictionPolicy, maxPrice) |> Spot |> Some }
    member this.Spot(state:VmConfig, evictionPolicy:EvictionPolicy) : VmConfig = this.Spot(state,(evictionPolicy, -1m))
    member this.Spot(state:VmConfig, maxPrice) : VmConfig = this.Spot(state,(Deallocate, maxPrice))
    /// Adds a SSD data disk to the VM with a specific size.
    [<CustomOperation "add_ssd_disk">]
    member this.AddSsd(state:VmConfig, size) = this.AddDisk(state, size, StandardSSD_LRS)
    /// Adds a conventional (non-SSD) data disk to the VM with a specific size.
    [<CustomOperation "add_slow_disk">]
    member this.AddSlowDisk(state:VmConfig, size) = this.AddDisk(state, size, Standard_LRS)
    /// Sets the prefix for the domain name of the VM.
    [<CustomOperation "domain_name_prefix">]
    member _.DomainNamePrefix(state:VmConfig, prefix) = { state with DomainNamePrefix = prefix }
    /// Sets the IP address prefix of the VM.
    [<CustomOperation "address_prefix">]
    member _.AddressPrefix(state:VmConfig, prefix) = { state with AddressPrefix = prefix }
    /// Sets the subnet prefix of the VM.
    [<CustomOperation "subnet_prefix">]
    member _.SubnetPrefix(state:VmConfig, prefix) = { state with SubnetPrefix = prefix }
    /// Sets the subnet name of the VM.
    [<CustomOperation "subnet_name">]
    member _.SubnetName(state:VmConfig, name:ResourceName) = { state with Subnet = Named (subnets.resourceId name) }
    member this.SubnetName(state:VmConfig, name) = this.SubnetName(state, ResourceName name)
    /// Uses an external VNet instead of creating a new one.
    [<CustomOperation "link_to_vnet">]
    member _.LinkToVNet(state:VmConfig, name:ResourceName) = { state with VNet = LinkedResource (Managed (virtualNetworks.resourceId name)) }
    member this.LinkToVNet(state:VmConfig, name) = this.LinkToVNet(state, ResourceName name)
    member this.LinkToVNet(state:VmConfig, vnet:Arm.Network.VirtualNetwork) = this.LinkToVNet(state, vnet.Name)
    member this.LinkToVNet(state:VmConfig, vnet:VirtualNetworkConfig) = this.LinkToVNet(state, vnet.Name)
    [<CustomOperation "link_to_unmanaged_vnet">]
    member _.LinkToUnmanagedVNet(state:VmConfig, name:ResourceName) = { state with VNet = LinkedResource (Unmanaged (virtualNetworks.resourceId name)) }
    member this.LinkToUnmanagedVNet(state:VmConfig, name) = this.LinkToUnmanagedVNet(state, ResourceName name)
    member this.LinkToUnmanagedVNet(state:VmConfig, vnet:Arm.Network.VirtualNetwork) = this.LinkToUnmanagedVNet(state, vnet.Name)
    member this.LinkToUnmanagedVNet(state:VmConfig, vnet:VirtualNetworkConfig) = this.LinkToUnmanagedVNet(state, vnet.Name)

    [<CustomOperation "custom_script">]
    member _.CustomScript(state:VmConfig, script:string) =
        match state.CustomScript with
        | None -> { state with CustomScript = Some script }
        | Some previousScript ->
            let firstScript = if script.Length > 10 then script.Substring(0, 10) + "..." else script
            let secondScript = if previousScript.Length > 10 then previousScript.Substring(0, 10) + "..." else previousScript
            raiseFarmer $"Only single custom_script execution is supported (and it can contain ARM-expressions). You have to merge your scripts. You have defined multiple custom_script: {firstScript} and {secondScript}"

    [<CustomOperation "custom_script_files">]
    member _.CustomScriptFiles(state:VmConfig, uris:string list) = { state with CustomScriptFiles = uris |> List.map Uri }

    interface ITaggable<VmConfig> with member _.Add state tags = { state with Tags = state.Tags |> Map.merge tags }
    interface IIdentity<VmConfig> with member _.Add state updater = { state with Identity = updater state.Identity }

    [<CustomOperation "custom_data">]
    member _.CustomData(state:VmConfig, customData:string) = { state with CustomData = Some customData }

    [<CustomOperation "disable_password_authentication">]
    member _.DisablePasswordAuthentication(state: VmConfig, disablePasswordAuthentication: bool) = { state with DisablePasswordAuthentication = Some disablePasswordAuthentication }

    [<CustomOperation "add_authorized_keys">]
    member _.AddAuthorizedKeys(state:VmConfig, sshObjects: (string * string) list) = { state with SshPathAndPublicKeys = Some sshObjects }
    [<CustomOperation "add_authorized_key">]
    member this.AddAuthorizedKey(state:VmConfig, path: string, keyData: string) = this.AddAuthorizedKeys(state, [(path, keyData)])
    /// Azure AD login extension may be enabled for Linux VM's.
    [<CustomOperation "aad_ssh_login">]
    member this.AadSshLoginEnabled(state:VmConfig, featureFlag:FeatureFlag) =
        { state with AadSshLogin = featureFlag }

    [<CustomOperation "public_ip">]
    /// Set the public IP for this VM
    member _.PublicIp(state: VmConfig, ref: ResourceRef<_> Option) = { state with PublicIp = ref}
    member _.PublicIp(state: VmConfig, ref: ResourceRef<_>) = { state with PublicIp = Some ref}
    member _.PublicIp(state: VmConfig, ref: LinkedResource) = { state with PublicIp = Some (LinkedResource ref)}
    member _.PublicIp(state: VmConfig, _ : Automatic) = { state with PublicIp = automaticPublicIp}

    [<CustomOperation "ip_allocation">]
    /// IP allocation method
    member _.IpAllocation(state: VmConfig, ref: PublicIpAddress.AllocationMethod Option) = { state with IpAllocation = ref}
    member _.IpAllocation(state: VmConfig, ref: PublicIpAddress.AllocationMethod) = { state with IpAllocation = Some ref}

    [<CustomOperation "private_ip_allocation">]
    /// IP allocation method
    member _.PrivateIpAllocation(state: VmConfig, ref: PrivateIpAddress.AllocationMethod Option) = { state with PrivateIpAllocation = ref}
    member _.PrivateIpAllocation(state: VmConfig, ref: PrivateIpAddress.AllocationMethod) = { state with PrivateIpAllocation = Some ref}

    /// Sets the network security group
    [<CustomOperation "network_security_group">]
    member _.NetworkSecurityGroup(state:VmConfig, nsg:IArmResource) =
        { state with NetworkSecurityGroup = Some (Managed nsg.ResourceId) }
    member _.NetworkSecurityGroup(state:VmConfig, nsg:ResourceId) =
        { state with NetworkSecurityGroup = Some (Managed nsg) }
    member _.NetworkSecurityGroup(state:VmConfig, nsg:NsgConfig) =
        { state with NetworkSecurityGroup = Some (Managed (nsg :> IBuilder).ResourceId) }
    /// Links the VM to an existing network security group.
    [<CustomOperation "link_to_network_security_group">]
    member _.LinkToNetworkSecurityGroup(state:VmConfig, nsg:IArmResource) =
        { state with NetworkSecurityGroup = Some (Unmanaged (nsg.ResourceId)) }
    member _.LinkToNetworkSecurityGroup(state:VmConfig, nsg:ResourceId) =
        { state with NetworkSecurityGroup = Some (Unmanaged nsg) }
    member _.LinkToNetworkSecurityGroup(state:VmConfig, nsg:NsgConfig) =
        { state with NetworkSecurityGroup = Some (Unmanaged (nsg :> IBuilder).ResourceId) }


let vm = VirtualMachineBuilder()

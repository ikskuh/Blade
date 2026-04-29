namespace Blade;

/// <summary>
/// Provides internal address-space-aware arithmetic for offsets expressed in address units.
/// </summary>
internal static class AddressMath
{
    /// <summary>
    /// Adds an offset measured in address units to a virtual address and returns a new virtual address.
    /// </summary>
    public static VirtualAddress AddAddressUnits(VirtualAddress address, int offset)
    {
        return address.AddressSpace switch
        {
            AddressSpace.Cog => new VirtualAddress(address.ToCogAddress() + offset),
            AddressSpace.Lut => new VirtualAddress(address.ToLutAddress() + offset),
            AddressSpace.Hub => new VirtualAddress(address.ToHubAddress() + offset),
            _ => Assert.UnreachableValue<VirtualAddress>($"Unsupported address space '{address.AddressSpace}'.") // pragma: force-coverage
        };
    }
}

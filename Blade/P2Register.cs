using System.Globalization;

namespace Blade;

public readonly record struct P2Register
{
    public P2Register(int address)
    {
        Address = Requires.InRange(address, 0, 0x1FF);
    }

    public P2Register(P2SpecialRegister register)
        : this((int)register)
    {
    }

    public int Address { get; }

    public bool IsSpecial => Address >= 0x1F0;

    public override string ToString()
    {
        if (IsSpecial)
            return ((P2SpecialRegister)Address).ToString();

        return $"r{Address.ToString(CultureInfo.InvariantCulture)}";
    }
}

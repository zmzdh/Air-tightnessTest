namespace AudioActuatorCanTest.Services
{
    public class HexRegion
    {
        public uint StartAddress { get; init; }

        public uint EndAddress { get; init; }

        public uint SizeBytes => EndAddress >= StartAddress ? EndAddress - StartAddress + 1 : 0;

        public string Range => $"0x{StartAddress:X8} - 0x{EndAddress:X8}";
    }
}

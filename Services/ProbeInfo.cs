namespace AudioActuatorCanTest.Services
{
    public class ProbeInfo
    {
        public int Index { get; set; }
        public string Name { get; set; }
        public string UniqueId { get; set; }

        public string DisplayName => $"{Index}: {Name} [{UniqueId}]";
    }
}

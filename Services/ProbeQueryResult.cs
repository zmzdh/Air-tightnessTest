using System.Collections.Generic;

namespace AudioActuatorCanTest.Services
{
    public record ProbeQueryResult(List<ProbeInfo> Probes, ProcessResult ProcessResult);
}

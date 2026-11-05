namespace Apex.Core.Engine;

// Only trusted local compositions can implement this friend-assembly capability. Neither a
// protocol platform name, JSON coverage nor EngineConfig.IsBacktest can mint this authority.
internal interface ITrustedSimulationGateway
{
    bool TrustedSimulation { get; }
}

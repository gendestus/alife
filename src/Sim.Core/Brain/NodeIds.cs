namespace Sim.Core.Brain;

/// <summary>Deterministic node id scheme (§4.4): id encodes what the node is, so alignment across genomes is by id alone.</summary>
public static class NodeIds
{
    public const int Bias = 0;
    public const long SensorBase = 1_000_000;
    public const long ActuatorBase = 2_000_000;
    public const int SlotsPerSensorGene = 8; // headroom above the largest sensor's slot count (VisionCreature = 5)

    public static int SensorInputNodeId(long sensorGeneId, int slot) =>
        (int)(SensorBase + sensorGeneId * SlotsPerSensorGene + slot);

    public static int ActuatorOutputNodeId(long actuatorGeneId) =>
        (int)(ActuatorBase + actuatorGeneId);
}

namespace Examples.Lib.physics;

/// <summary>
/// World gravity is off; a radial force pulls 200 moons onto a planet.
/// </summary>
[Guid("11d21b6c-fe50-5d04-8a57-b888f5b276e1")]
internal sealed class PhysicsPlanetExample : Instance<PhysicsPlanetExample>
{
    [Output(Guid = "1873fe99-98a8-593f-ae1d-d07e401ae71c")]
    public readonly Slot<Texture2D> Output = new();
}

using MelonLoader;

[assembly: MelonInfo(typeof(AI_Train.Core), "AI_Train", "1.0.0", "josfa", null)]
[assembly: MelonGame("Buckethead Entertainment", "RUMBLE")]

namespace AI_Train;

public sealed class Core : MelonMod
{
    private TrainingFoundation _foundation;

    public override void OnInitializeMelon()
    {
        _foundation = new TrainingFoundation();
        _foundation.Initialize();
    }

    public override void OnUpdate()
    {
        _foundation?.OnUpdate();
    }

    public override void OnSceneWasLoaded(int buildIndex, string sceneName)
    {
        _foundation?.OnSceneWasLoaded(buildIndex, sceneName);
    }

    public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
    {
        _foundation?.OnSceneWasUnloaded(buildIndex, sceneName);
    }

    public override void OnDeinitializeMelon()
    {
        _foundation?.Dispose();
    }
}

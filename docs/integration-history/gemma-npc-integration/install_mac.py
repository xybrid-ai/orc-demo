"""Install the dialogue scripts in a stopped Unity Editor; never build or save a scene."""
from pathlib import Path
import hashlib,json,shutil,time

def install(project, source, baselines):
    project=Path(project); source=Path(source)
    runtime=project/'Assets/XybridAvatarDemo/Runtime'
    backup=project/'Library/XybridAvatarDemo/Backups'/('gemma-npc-'+str(int(time.time())))
    for name, digest in baselines.items():
        f=runtime/name
        if hashlib.sha256(f.read_text().rstrip().encode()).hexdigest()!=digest:
            raise RuntimeError('Source changed since inspection: '+name)
    backup.mkdir(parents=True,exist_ok=True)
    # Two additive patches to the current Mac sources, keeping newer voice edits.
    patches={}
    scenario=(runtime/'GatekeeperScenario.cs').read_text()
    if 'debugUsePlaceholderReplies' not in scenario:
        scenario=scenario.replace('public bool debugStartInFreeForm;', 'public bool debugStartInFreeForm;\n    public bool debugUsePlaceholderReplies;')
    patches['GatekeeperScenario.cs']=scenario
    controls=(runtime/'GatekeeperSpeechControls.cs').read_text()
    controls=controls.replace('Screen.height - 210, 460, 200','Screen.height - 270, 460, 260')
    anchor='        var data = controller.LastGenerated;'
    addition='''        GUILayout.BeginHorizontal();
        GUILayout.Label("Replies: " + (controller.scenario.debugUsePlaceholderReplies ? "Placeholder (debug)" : "Gemma4NPC / local Q4"));
        if (GUILayout.Button(controller.scenario.debugUsePlaceholderReplies ? "Use model" : "Debug placeholder"))
        {
            controller.scenario.debugUsePlaceholderReplies = !controller.scenario.debugUsePlaceholderReplies;
            controller.Restart();
        }
        GUILayout.EndHorizontal();
        var npc = controller.GetComponent<GatekeeperNpcModel>();
        if (npc != null && !string.IsNullOrEmpty(npc.LastError)) GUILayout.Label("NPC: " + npc.LastError);
'''
    if 'Debug placeholder' not in controls:
        if anchor not in controls: raise RuntimeError('Voice panel insertion point changed.')
        controls=controls.replace(anchor,addition+anchor)
    patches['GatekeeperSpeechControls.cs']=controls
    for name,content in patches.items():
        dest=runtime/name;shutil.copy2(dest,backup/name);dest.write_text(content)
    for folder in ['Runtime','Editor']:
        for src in (source/folder).glob('*.cs'):
            dest=project/'Assets/XybridAvatarDemo'/folder/src.name
            if dest.exists():shutil.copy2(dest,backup/src.name)
            shutil.copy2(src,dest)
    models=project/'Assets/StreamingAssets/XybridModels/gemma4npc-e2b-q4'
    models.mkdir(parents=True,exist_ok=True)
    shutil.copy2(source/'model_metadata.json',models/'model_metadata.json')
    print('Installed scripts. Backup:',backup)

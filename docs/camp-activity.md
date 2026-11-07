# Life in the visible camp

The road approach shows two different lookouts taking turns watching the road and the camp. Through the open gate,
a resident drinks from a mug beside the existing hearth, a seated companion tells a story and laughs, and another
walks a short route in front of the right tent, pausing to stretch or yawn. The seat, supper spit, pot, provisions,
bedroll and weapon rack make this a used camp. A larger tent frames the view beyond the hearth. Watch lights,
a small hearth bounce light and drifting smoke complete the dusk composition.

`CampResident` samples authored humanoid clips as a looping sequence of beats. Each beat names a clip, duration,
playback rate, repeat setting and mark in the camp group's space. Consecutive clips cross-fade. Walking uses an
in-place stride at a matched travel speed, easing in and out with the animation; the routine supplies root placement
and heading. The clock keeps running off screen and behind the shut doors, so a walker can move back into view.
Bone sampling resumes as soon as the gate begins opening and the resident enters the camera frustum.

The background rigs have no speech, encounter behaviours, colliders or root motion. They share their imported
meshes and materials. Different GoblinFighter variants, routines and phases keep them distinct from Trola and each
other. The drinking mug follows the right hand throughout its hold, sip and swig clips. The seat height is fitted
to the retargeted sitting pose; the talking and laughing clips remain seated throughout. The walker stays beside
the right tent and never crosses the entry path or gate swing. Short plank extensions at the front of each
watchtower support the lookouts beside the hanging cloth.

**Xybrid → Avatar Demo → Build Camp Activity** rebuilds only `Village Gate Set/Camp activity` in the open
`GoblinSpeakingDemo` scene. It resolves its required assets before replacing the old group. Adjust or disable the
residents, routines, lights and props there. The saved scene contains poses and asset references only.

Restore the dependencies recorded in `local-assets.json` from a licensed installation, preserving all `.meta`
GUIDs, then run `python3 tools/local_assets.py`. In addition to the existing Goblin War Camp and Sidekick assets,
this pass uses Synty **Animation Idles**, the Base Locomotion in-place walk, customized **GoblinFighter_01, _02,
_04 and _05**, and the locally supplied Mixamo **Sitting Talking** and **Sitting Laughing** clips under
`Assets/Animations`. Acquire these under their respective terms; imported clips, customized meshes and pack
binaries stay local. A stock pack import may not reproduce the customized characters or humanoid import settings.
The optional assets examined during development are not required unless listed in the manifest.

TrolaAtmosphere remains the camera writer. The existing camp soundscape supplies the distant voices and hearth
sound. The encounter's camera routes, speech providers and SDK version remain configured as before.

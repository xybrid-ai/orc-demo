# Life in the visible camp

The approach shows lookouts in the watchtowers, with small pools of torchlight and smoke rising from the existing
campfire behind the gate. When the doors open, a resident beside that fire warms their hands. Activity is authored
only in the tower openings and the first interior view; there is no patrol route or simulated village beyond the set.
Short plank extensions at the front of each watchtower support the lookouts beside the hanging cloth.

`CampResident` samples the already-required standing idle with different phases and speeds. Lookouts turn their
heads and shoulders and occasionally raise a hand to their brow; the fireside resident leans toward the warmth.
The gestures use the rig's bones after each fresh idle sample. Root position, rotation and scale stay fixed, and
procedural bone offsets are restored before the next sample and when the component is disabled.

The source is the restored, customized `GoblinFighter_03` prefab, shared with Trola. Background residents have their
own presentation component and share the existing mesh, material and standing clip. They have no speech, encounter
behaviours or colliders. Animation pauses outside the main camera's frustum; the interior resident waits while the
gate is closed. The existing camp soundscape supplies the distant voices and hearth sound.

**Xybrid → Avatar Demo → Build Camp Activity** rebuilds only `Village Gate Set/Camp activity`. It requires the
manifest-listed character, idle and torch plus the existing village set. It leaves the road dressing and the rest
of the scene in place. Residents, watch lights and smoke can be adjusted or disabled in that group. The scene
contains prefab and clip references; licensed art and customized meshes remain local dependencies.

This pass keeps the camera routes, lens, portrait lighting, encounter rules, speech providers and SDK version as
configured. TrolaAtmosphere remains the camera writer. No new voice or animation package is needed.

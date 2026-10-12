# Integration history

These two source snapshots were moved from the Xybrid runtime repository after
extracting the standalone orc demo. They document the Gemma NPC integration and
the direct Kitten rc.4 speech work; they are archived reference material.

The maintained implementation lives in `Assets/XybridAvatarDemo/Runtime` and
`Assets/XybridAvatarDemo/Editor`. Current setup instructions are in the project
root README, and current provisioning tools are in the root `tools` directory.

Do not install these old snapshots over the current demo. Their installers target
the original Xybrid Tavern project and their baselines can predate later fixes.
Historical Editor/build/measurement instructions are not the current workflow.
The spike's SDK adapter also does not mean SDK 0.10.1 is installed.

Only source, metadata and documentation were migrated: no pack assets, voices,
models, native binaries or generated test outputs. The archived clean script was
adapted to work outside the runtime repository's shared cleaning library.

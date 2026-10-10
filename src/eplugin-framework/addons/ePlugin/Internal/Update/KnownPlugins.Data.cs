#if TOOLS
namespace Enaweg.Plugin.Internal.Update;

// Generated on 2026-10-10 from tools/godot_addons_active_100_stars.json (built by tools/build_godot_addons.cs): popular
// add-ons with at least 100 stars and a commit within the last year. Each slug is the plugin folder of the add-on's
// latest release, installed the way ePlugin's updater installs it and validated by AddonPackageValidator. Add-ons
// without a stable release, with a GDExtension, or whose package the updater refuses are left out.
internal static partial class KnownPlugins
{
    private static KnownPlugin[] Entries() =>
    [
        // https://github.com/FlamxGames/godot-ai-assistant-hub, verified with release v2.1.0
        new("ai_assistant_hub", "AI Assistant Hub", "https://github.com/FlamxGames/godot-ai-assistant-hub/releases",
            "https://github.com/FlamxGames/godot-ai-assistant-hub#readme", "https://github.com/FlamxGames/godot-ai-assistant-hub", "https://github.com/FlamxGames/godot-ai-assistant-hub"),
        // https://github.com/anthonyec/godot_little_camera_preview, verified with release v1.4
        new("anthonyec.camera_preview", "Little Camera Preview", "https://github.com/anthonyec/godot_little_camera_preview/releases",
            "https://github.com/anthonyec/godot_little_camera_preview#readme", "https://github.com/anthonyec/godot_little_camera_preview", "https://github.com/anthonyec/godot_little_camera_preview"),
        // https://github.com/godot-extended-libraries/godot-antialiased-line2d, verified with release v1.2.0
        new("antialiased_line2d", "Antialiased Line2D - Better antialiasing for 2D line/polygon/circle drawing", "https://github.com/godot-extended-libraries/godot-antialiased-line2d/releases",
            "https://github.com/godot-extended-libraries/godot-antialiased-line2d#readme", "https://github.com/Calinou/godot-antialiased-line2d-demo", "https://github.com/godot-extended-libraries/godot-antialiased-line2d"),
        // https://github.com/bitbrain/beehave, verified with release v2.9.3
        new("beehave", "Beehave - behavior tree AI for Godot Engine", "https://github.com/bitbrain/beehave/releases",
            "https://github.com/bitbrain/beehave#readme", "https://bitbra.in/beehave", "https://github.com/bitbrain/beehave"),
        // https://github.com/ThePat02/BehaviourToolkit, verified with release 2.0.4
        new("behaviour_toolkit", "BehaviourToolkit - StateMachine and Behaviour Tree for AI & NPCs", "https://github.com/ThePat02/BehaviourToolkit/releases",
            "https://github.com/ThePat02/BehaviourToolkit#readme", "https://github.com/ThePat02/BehaviourToolkit", "https://github.com/ThePat02/BehaviourToolkit"),
        // https://github.com/endlessm/godot-block-coding, verified with release v0.10.1
        new("block_code", "Block Coding", "https://github.com/endlessm/godot-block-coding/releases",
            "https://github.com/endlessm/godot-block-coding#readme", "https://godotengine.org/asset-library/asset/3095", "https://github.com/endlessm/godot-block-coding"),
        // https://github.com/CodeAndWeb/texturepacker-godot-plugin, verified with release v4.3.0
        new("codeandweb.texturepacker", "TexturePacker Importer (SpriteSheet, AtlasTexture)", "https://github.com/CodeAndWeb/texturepacker-godot-plugin/releases",
            "https://github.com/CodeAndWeb/texturepacker-godot-plugin#readme", "https://github.com/CodeAndWeb/texturepacker-godot-plugin", "https://github.com/CodeAndWeb/texturepacker-godot-plugin"),
        // https://github.com/rsubtil/controller_icons, verified with release v3.1.8
        new("controller_icons", "Controller Icons", "https://github.com/rsubtil/controller_icons/releases",
            "https://github.com/rsubtil/controller_icons#readme", "https://godotengine.org/asset-library/asset/2565", "https://github.com/rsubtil/controller_icons"),
        // https://github.com/SpockBauru/CSG_Terrain, verified with release v1.1.1
        new("csg_terrain", "CSG Terrain", "https://github.com/SpockBauru/CSG_Terrain/releases",
            "https://github.com/SpockBauru/CSG_Terrain#readme", "https://github.com/SpockBauru/CSG_Terrain", "https://github.com/SpockBauru/CSG_Terrain"),
        // https://github.com/fabian-becker/CSG_Toolkit, verified with release 1.8.0
        new("csg_toolkit", "CSG Toolkit - Enhance Your Blockout Speed", "https://github.com/fabian-becker/CSG_Toolkit/releases",
            "https://github.com/fabian-becker/CSG_Toolkit#readme", "https://github.com/fabian-becker/CSG_Toolkit", "https://github.com/fabian-becker/CSG_Toolkit"),
        // https://github.com/godot-extended-libraries/godot-debug-menu, verified with release v1.2.0
        new("debug_menu", "Debug Menu - Display in-game FPS/performance/hardware metrics", "https://github.com/godot-extended-libraries/godot-debug-menu/releases",
            "https://github.com/godot-extended-libraries/godot-debug-menu#readme", "https://github.com/godot-extended-libraries/godot-debug-menu-demo", "https://github.com/godot-extended-libraries/godot-debug-menu"),
        // https://github.com/cloudofoz/godot-deformablemesh, verified with release v0.30
        new("deformablemesh", "DeformableMesh", "https://github.com/cloudofoz/godot-deformablemesh/releases",
            "https://github.com/cloudofoz/godot-deformablemesh#readme", "https://github.com/cloudofoz/godot-deformablemesh", "https://github.com/cloudofoz/godot-deformablemesh"),
        // https://github.com/nathanhoad/godot_dialogue_manager, verified with release v4.1.0
        new("dialogue_manager", "Dialogue Manager", "https://github.com/nathanhoad/godot_dialogue_manager/releases",
            "https://github.com/nathanhoad/godot_dialogue_manager#readme", "https://dialogue.nathanhoad.net/", "https://github.com/nathanhoad/godot_dialogue_manager"),
        // https://github.com/nagidev/DialogueNodes, verified with release v1.2
        new("dialogue_nodes", "Dialogue Nodes", "https://github.com/nagidev/DialogueNodes/releases",
            "https://github.com/nagidev/DialogueNodes#readme", "https://github.com/nagidev/DialogueNodes", "https://github.com/nagidev/DialogueNodes"),
        // https://github.com/3ddelano/discord.gd, verified with release 2.0.6
        new("discord_gd", "Discord.gd", "https://github.com/3ddelano/discord.gd/releases",
            "https://github.com/3ddelano/discord.gd#readme", "https://github.com/3ddelano/discord.gd", "https://github.com/3ddelano/discord.gd"),
        // https://github.com/YuriSizov/godot-editor-theme-explorer, verified with release v2.1.1
        new("explore-editor-theme", "Editor Theme Explorer", "https://github.com/YuriSizov/godot-editor-theme-explorer/releases",
            "https://github.com/YuriSizov/godot-editor-theme-explorer#readme", "https://codeberg.org/YuriSizov/godot-editor-theme-explorer", "https://github.com/YuriSizov/godot-editor-theme-explorer"),
        // https://github.com/imjp94/gd-blender-3d-shortcuts, verified with release 0.4.0
        new("gd-blender-3d-shortcuts", "Blender 3D Shortcuts", "https://github.com/imjp94/gd-blender-3d-shortcuts/releases",
            "https://github.com/imjp94/gd-blender-3d-shortcuts#readme", "https://github.com/imjp94/gd-blender-3d-shortcuts", "https://github.com/imjp94/gd-blender-3d-shortcuts"),
        // https://github.com/godot-gdunit-labs/gdUnit4, verified with release v6.2.2
        new("gdUnit4", "GdUnit4", "https://github.com/godot-gdunit-labs/gdUnit4/releases",
            "https://github.com/godot-gdunit-labs/gdUnit4#readme", "https://godot-gdunit-labs.github.io/gdUnit4/latest/", "https://github.com/godot-gdunit-labs/gdUnit4"),
        // https://github.com/peter-kish/gloot, verified with release v3.0.2
        new("gloot", "GLoot (Universal Inventory System)", "https://github.com/peter-kish/gloot/releases",
            "https://github.com/peter-kish/gloot#readme", "https://github.com/peter-kish/gloot", "https://github.com/peter-kish/gloot"),
        // https://github.com/Structed/godot-playfab, verified with release 1.3.1
        new("godot-playfab", "godot-playfab", "https://github.com/Structed/godot-playfab/releases",
            "https://github.com/Structed/godot-playfab#readme", "https://github.com/Structed/godot-playfab", "https://github.com/Structed/godot-playfab"),
        // https://github.com/GodotVR/godot-xr-tools, verified with release 4.5.2
        new("godot-xr-tools", "Godot Meta Toolkit", "https://github.com/GodotVR/godot-xr-tools/releases",
            "https://github.com/GodotVR/godot-xr-tools#readme", "https://github.com/GodotVR/godot-xr-tools", "https://github.com/GodotVR/godot-xr-tools"),
        // https://github.com/addmix/godot_aerodynamic_physics, verified with release v0.9.1
        new("godot_aerodynamic_physics", "Godot Aerodynamic Physics", "https://github.com/addmix/godot_aerodynamic_physics/releases",
            "https://github.com/addmix/godot_aerodynamic_physics#readme", "https://godotengine.org/asset-library/asset/2580", "https://github.com/addmix/godot_aerodynamic_physics"),
        // https://github.com/derkork/godot-resource-groups, verified with release v0.4.2
        new("godot_resource_groups", "Godot Resource Groups", "https://github.com/derkork/godot-resource-groups/releases",
            "https://github.com/derkork/godot-resource-groups#readme", "https://github.com/derkork/godot-resource-groups", "https://github.com/derkork/godot-resource-groups"),
        // https://github.com/derkork/godot-statecharts, verified with release v0.22.5
        new("godot_state_charts", "Godot State Charts", "https://github.com/derkork/godot-statecharts/releases",
            "https://github.com/derkork/godot-statecharts#readme", "https://derkork.github.io/godot-statecharts/", "https://github.com/derkork/godot-statecharts"),
        // https://github.com/paulloz/godot-ink, verified with release v1.1.2
        new("GodotInk", "ink", "https://github.com/paulloz/godot-ink/releases",
            "https://github.com/paulloz/godot-ink#readme", "https://github.com/paulloz/godot-ink", "https://github.com/paulloz/godot-ink"),
        // https://github.com/godot-sdk-integrations/godot-play-game-services, verified with release v3.4.0
        new("GodotPlayGameServices", "Google Play Games Services for Godot", "https://github.com/godot-sdk-integrations/godot-play-game-services/releases",
            "https://github.com/godot-sdk-integrations/godot-play-game-services#readme", "https://github.com/godot-sdk-integrations/godot-play-game-services", "https://github.com/godot-sdk-integrations/godot-play-game-services"),
        // https://github.com/godotneers/G.U.I.D.E, verified with release v0.14.0
        new("guide", "G.U.I.D.E - Godot Unified Input Detection Engine", "https://github.com/godotneers/G.U.I.D.E/releases",
            "https://github.com/godotneers/G.U.I.D.E#readme", "https://godotneers.github.io/G.U.I.D.E/", "https://github.com/godotneers/G.U.I.D.E"),
        // https://github.com/bitwes/Gut, verified with release v9.6.1
        new("gut", "Gut - Godot Unit Testing", "https://github.com/bitwes/Gut/releases",
            "https://github.com/bitwes/Gut#readme", "https://github.com/bitwes/Gut", "https://github.com/bitwes/Gut"),
        // https://github.com/cluttered-code/godot-health-hitbox-hurtbox, verified with release v5.0.4
        new("health_hitbox_hurtbox", "Health, HitBoxes, HurtBoxes and HitScans", "https://github.com/cluttered-code/godot-health-hitbox-hurtbox/releases",
            "https://github.com/cluttered-code/godot-health-hitbox-hurtbox#readme", "https://github.com/cluttered-code/godot-health-hitbox-hurtbox", "https://github.com/cluttered-code/godot-health-hitbox-hurtbox"),
        // https://github.com/NitroxNova/humanizer, verified with release v2.2.0
        new("humanizer", "Humanizer", "https://github.com/NitroxNova/humanizer/releases",
            "https://github.com/NitroxNova/humanizer#readme", "https://github.com/NitroxNova/humanizer", "https://github.com/NitroxNova/humanizer"),
        // https://github.com/imjp94/gd-YAFSM, verified with release 0.6.3
        new("imjp94.yafsm", "gd-YAFSM(Finite State Machine)", "https://github.com/imjp94/gd-YAFSM/releases",
            "https://github.com/imjp94/gd-YAFSM#readme", "https://github.com/imjp94/gd-YAFSM", "https://github.com/imjp94/gd-YAFSM"),
        // https://github.com/ReunMedia/godot-levelblock, verified with release 3.0.2
        new("level_block", "LevelBlock", "https://github.com/ReunMedia/godot-levelblock/releases",
            "https://github.com/ReunMedia/godot-levelblock#readme", "https://github.com/ReunMedia/godot-levelblock", "https://github.com/ReunMedia/godot-levelblock"),
        // https://github.com/limbonaut/limbo_console, verified with release v0.8.0
        new("limbo_console", "LimboConsole: in-game dev console", "https://github.com/limbonaut/limbo_console/releases",
            "https://github.com/limbonaut/limbo_console#readme", "https://github.com/limbonaut/limbo_console", "https://github.com/limbonaut/limbo_console"),
        // https://github.com/Shiva-Shadowsong/loggie, verified with release 3.0
        new("loggie", "Loggie", "https://github.com/Shiva-Shadowsong/loggie/releases",
            "https://github.com/Shiva-Shadowsong/loggie#readme", "https://github.com/Shiva-Shadowsong/loggie", "https://github.com/Shiva-Shadowsong/loggie"),
        // https://github.com/daenvil/MarkdownLabel, verified with release v1.4.0
        new("markdownlabel", "MarkdownLabel", "https://github.com/daenvil/MarkdownLabel/releases",
            "https://github.com/daenvil/MarkdownLabel#readme", "https://github.com/daenvil/MarkdownLabel", "https://github.com/daenvil/MarkdownLabel"),
        // https://github.com/nklbdev/godot-4-importality, verified with release 0.4.0
        new("nklbdev.importality", "Importality", "https://github.com/nklbdev/godot-4-importality/releases",
            "https://github.com/nklbdev/godot-4-importality#readme", "https://github.com/nklbdev/godot-4-importality", "https://github.com/nklbdev/godot-4-importality"),
        // https://github.com/paulloz/godot-colorblindness, verified with release v1.6
        new("paulloz.colorblindness", "Colorblindness", "https://github.com/paulloz/godot-colorblindness/releases",
            "https://github.com/paulloz/godot-colorblindness#readme", "https://github.com/paulloz/godot-colorblindness", "https://github.com/paulloz/godot-colorblindness"),
        // https://github.com/HungryProton/scatter, verified with release 4.0
        new("proton_scatter", "ProtonScatter", "https://github.com/HungryProton/scatter/releases",
            "https://github.com/HungryProton/scatter#readme", "https://github.com/HungryProton/scatter", "https://github.com/HungryProton/scatter"),
        // https://github.com/nathanhoad/godot_puzzle_dependencies, verified with release v3.0.1
        new("puzzle_dependencies", "Puzzle Dependencies", "https://github.com/nathanhoad/godot_puzzle_dependencies/releases",
            "https://github.com/nathanhoad/godot_puzzle_dependencies#readme", "https://github.com/nathanhoad/godot_puzzle_dependencies", "https://github.com/nathanhoad/godot_puzzle_dependencies"),
        // https://github.com/Neroware/GodotRx, verified with release v1.0.3
        new("reactivex", "GodotRx", "https://github.com/Neroware/GodotRx/releases",
            "https://github.com/Neroware/GodotRx#readme", "https://github.com/Neroware/GodotRx", "https://github.com/Neroware/GodotRx"),
        // https://github.com/SirRamEsq/SmartShape2D, verified with release 3.3.2
        new("rmsmartshape", "RMSmartShape2D", "https://github.com/SirRamEsq/SmartShape2D/releases",
            "https://github.com/SirRamEsq/SmartShape2D#readme", "https://github.com/SirRamEsq/SmartShape2D", "https://github.com/SirRamEsq/SmartShape2D"),
        // https://github.com/TheDuckCow/godot-road-generator, verified with release 0.9.4
        new("road-generator", "Godot Road Generator", "https://github.com/TheDuckCow/godot-road-generator/releases",
            "https://github.com/TheDuckCow/godot-road-generator#readme", "https://github.com/TheDuckCow/godot-road-generator", "https://github.com/TheDuckCow/godot-road-generator"),
        // https://github.com/Rubonnek/dialogue-engine, verified with release v1.6.0
        new("rubonnek.dialogue_engine", "Dialogue Engine", "https://github.com/Rubonnek/dialogue-engine/releases",
            "https://github.com/Rubonnek/dialogue-engine#readme", "https://github.com/Rubonnek/dialogue-engine", "https://github.com/Rubonnek/dialogue-engine"),
        // https://github.com/Rubonnek/quest-manager, verified with release v1.3.2
        new("rubonnek.quest_manager", "Quest Manager", "https://github.com/Rubonnek/quest-manager/releases",
            "https://github.com/Rubonnek/quest-manager#readme", "https://github.com/Rubonnek/quest-manager", "https://github.com/Rubonnek/quest-manager"),
        // https://github.com/derkork/godot-safe-resource-loader, verified with release v0.3.0
        new("safe_resource_loader", "Godot Safe Resource Loader", "https://github.com/derkork/godot-safe-resource-loader/releases",
            "https://github.com/derkork/godot-safe-resource-loader#readme", "https://github.com/derkork/godot-safe-resource-loader", "https://github.com/derkork/godot-safe-resource-loader"),
        // https://github.com/glass-brick/Scene-Manager, verified with release v2.1.0
        new("scene_manager", "Scene Manager", "https://github.com/glass-brick/Scene-Manager/releases",
            "https://github.com/glass-brick/Scene-Manager#readme", "https://github.com/glass-brick/Scene-Manager", "https://github.com/glass-brick/Scene-Manager"),
        // https://github.com/Maran23/script-ide, verified with release 2.2.5
        new("script-ide", "Script-IDE", "https://github.com/Maran23/script-ide/releases",
            "https://github.com/Maran23/script-ide#readme", "https://maran.tools/url/script-ide", "https://github.com/Maran23/script-ide"),
        // https://github.com/TheWalruzz/godot-sx, verified with release 1.9.2
        new("signal_extensions", "GodotSx - Signal Extensions", "https://github.com/TheWalruzz/godot-sx/releases",
            "https://github.com/TheWalruzz/godot-sx#readme", "https://github.com/TheWalruzz/godot-sx", "https://github.com/TheWalruzz/godot-sx"),
        // https://github.com/Ericdowney/SignalVisualizer, verified with release 1.8.0
        new("SignalVisualizer", "SignalVisualizer", "https://github.com/Ericdowney/SignalVisualizer/releases",
            "https://github.com/Ericdowney/SignalVisualizer#readme", "https://github.com/Ericdowney/SignalVisualizer", "https://github.com/Ericdowney/SignalVisualizer"),
        // https://github.com/murikistudio/simple-gui-transitions, verified with release v0.5.0
        new("simple-gui-transitions", "Simple GUI Transitions", "https://github.com/murikistudio/simple-gui-transitions/releases",
            "https://github.com/murikistudio/simple-gui-transitions#readme", "https://godotengine.org/asset-library/asset/2134", "https://github.com/murikistudio/simple-gui-transitions"),
        // https://github.com/sketchfab/godot-plugin, verified with release 1.1.0
        new("sketchfab", "Sketchfab", "https://github.com/sketchfab/godot-plugin/releases",
            "https://github.com/sketchfab/godot-plugin#readme", "https://github.com/sketchfab/godot-plugin", "https://github.com/sketchfab/godot-plugin"),
        // https://github.com/nathanhoad/godot_sound_manager, verified with release v2.6.2
        new("sound_manager", "Sound Manager", "https://github.com/nathanhoad/godot_sound_manager/releases",
            "https://github.com/nathanhoad/godot_sound_manager#readme", "https://github.com/nathanhoad/godot_sound_manager", "https://github.com/nathanhoad/godot_sound_manager"),
        // https://github.com/98teg/SpriteMesh, verified with release v2.1.0
        new("sprite_mesh", "SpriteMesh", "https://github.com/98teg/SpriteMesh/releases",
            "https://github.com/98teg/SpriteMesh#readme", "https://github.com/98teg/SpriteMesh", "https://github.com/98teg/SpriteMesh"),
        // https://github.com/TaloDev/godot, verified with release 1.2.0
        new("talo", "Talo Game Services: open-source player management, leaderboards and stats", "https://github.com/TaloDev/godot/releases",
            "https://github.com/TaloDev/godot#readme", "https://trytalo.com/godot", "https://github.com/TaloDev/godot"),
        // https://github.com/sempitern0/Terrainy, verified with release 1.5.0
        new("terrainy", "Terrainy", "https://github.com/sempitern0/Terrainy/releases",
            "https://github.com/sempitern0/Terrainy#readme", "https://godotengine.org/asset-library/asset/3438", "https://github.com/sempitern0/Terrainy"),
        // https://github.com/Inspiaaa/ThemeGen, verified with release v1.4.0
        new("theme_gen_save_sync", "ThemeGen", "https://github.com/Inspiaaa/ThemeGen/releases",
            "https://github.com/Inspiaaa/ThemeGen#readme", "https://godotengine.org/asset-library/asset/3299", "https://github.com/Inspiaaa/ThemeGen"),
        // https://github.com/OrigamiDev-Pete/TODO_Manager, verified with release v2.7.0
        new("Todo_Manager", "TODO Manager (Godot 3.x)", "https://github.com/OrigamiDev-Pete/TODO_Manager/releases",
            "https://github.com/OrigamiDev-Pete/TODO_Manager#readme", "https://github.com/OrigamiDev-Pete/TODO_Manager", "https://github.com/OrigamiDev-Pete/TODO_Manager"),
        // https://github.com/kanimaru/twitcher, verified with release 2.5.1
        new("twitcher", "Twitcher", "https://github.com/kanimaru/twitcher/releases",
            "https://github.com/kanimaru/twitcher#readme", "https://twitcher.kani.dev/", "https://github.com/kanimaru/twitcher"),
        // https://github.com/ClarkThyLord/Voxel-Core, verified with release v3.2.0
        new("voxel-core", "Voxel-Core", "https://github.com/ClarkThyLord/Voxel-Core/releases",
            "https://github.com/ClarkThyLord/Voxel-Core#readme", "https://godotengine.org/asset-library/asset/465", "https://github.com/ClarkThyLord/Voxel-Core"),
        // https://github.com/detomon/wigglebone, verified with release v3.0.0
        new("wigglebone", "WiggleBone", "https://github.com/detomon/wigglebone/releases",
            "https://github.com/detomon/wigglebone#readme", "https://github.com/detomon/wigglebone", "https://github.com/detomon/wigglebone"),
        // https://github.com/Kiamo2/YATI, verified with release v2.2.10
        new("YATI", "YATI (Yet Another Tiled Importer) for Godot 4", "https://github.com/Kiamo2/YATI/releases",
            "https://github.com/Kiamo2/YATI#readme", "https://github.com/Kiamo2/YATI", "https://github.com/Kiamo2/YATI"),
        // https://github.com/Joy-less/YouCanDoIt, verified with release v4.4
        new("YouCanDoIt", "You Can Do It!", "https://github.com/Joy-less/YouCanDoIt/releases",
            "https://github.com/Joy-less/YouCanDoIt#readme", "https://github.com/Joy-less/YouCanDoIt", "https://github.com/Joy-less/YouCanDoIt"),
        // https://github.com/Zylann/godot_editor_debugger_plugin, verified with release 0.4
        new("zylann.editor_debugger", "Editor debugger", "https://github.com/Zylann/godot_editor_debugger_plugin/releases",
            "https://github.com/Zylann/godot_editor_debugger_plugin#readme", "https://github.com/Zylann/godot_editor_debugger_plugin", "https://github.com/Zylann/godot_editor_debugger_plugin"),
    ];
}
#endif

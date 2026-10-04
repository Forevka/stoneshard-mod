using CoreLoader;

[assembly: CoreModInfo(typeof(ObjectTypeProbe.Probe), "Object type probe", "1.0.0", "Lodestone tests")]
[assembly: CoreModAnyGame]

namespace ObjectTypeProbe;

/// <summary>
/// Regression fixture for runtime-defined objects (<see cref="ObjectTypes"/>).
/// </summary>
/// <remarks>
/// Three types: a base with C# Create, Step, Alarm 0, User 0, Draw GUI, Destroy
/// and Clean Up; a child of it with only its own Create (which calls the
/// inherited one); and a collider whose Collision event names the base. One
/// base and one child are spawned far outside the room, and a collider on top
/// of the child only. After a second the probe checks, through the game's own
/// builtins, that the objects exist with their names and parent, that
/// instance counts and collision queries against the base include the child,
/// and that every event ran - the child's Step, Alarm and User events being the
/// base's, inherited. Then it destroys the collider (Destroy and Clean Up must
/// run) and logs PASSED, or FAILED with the checks that failed.
///
/// Hot reload: copying the dll in again tears the first run down (the loader
/// destroys every instance of the unloading mod's types) and runs it again.
/// The second run first checks that no instance of its types survived, and
/// that defining the same names gives back the same objects.
/// </remarks>
public sealed class Probe : CoreMod
{
    private const string BaseName = "o_lodestone_probe";
    private const string ChildName = "o_lodestone_probe_child";
    private const string ColliderName = "o_lodestone_probe_collider";
    private const double Far = -20000;   // outside any room: nothing of the game is there

    // Static: a hot reload is a new assembly, so these start over with it.
    private static int _destroyed, _cleanedUp, _collisions, _drawGui;

    private ObjectType? _base, _child, _collider;
    private InstanceRef _b, _c, _k;
    private int _frame, _startFrame = -1;
    private bool _done;
    private readonly List<string> _failures = new();

    private void Expect(bool ok, string what)
    {
        if (!ok) _failures.Add(what);
    }

    public override void OnUpdate()
    {
        if (_done) return;
        _frame++;
        if (_base == null)
        {
            string status = ObjectTypes.Status;
            if (status.StartsWith("not proven", StringComparison.Ordinal))
            {
                // Some games run no code on their title screen until a key is pressed.
                if (_frame == 1800) { Log.Warning($"object types still {status} after 30 s; probe not run"); _done = true; }
                return;
            }
            if (!ObjectTypes.Available)
            {
                Log.Warning($"object types unavailable here: {status}");
                _done = true;
                return;
            }
            Start();
            return;
        }
        if (_frame - _startFrame == 60) Check();
        if (_frame - _startFrame == 62) Finish();
    }

    // An asset index from a builtin's answer: a number, or a 2024+ typed reference.
    private static int Index(RValue v) => v.IsNumber ? (int)v.AsReal : v.Kind == RValueKind.Reference ? (int)(v.Int64 & 0xFFFFFFFF) : -1;

    // An instance came back: an id (a number from 100000 up) or a 2024+ instance reference, not noone (-4).
    private static bool Found(RValue v) => v.Kind == RValueKind.Reference ? (int)(v.Int64 & 0xFFFFFFFF) >= 0 : v.IsNumber && v.AsReal >= 0;

    private static bool PhysicsGame()
    {
        for (int i = 0; i < 300; i++)
            if (Game.CallBuiltin("object_exists", i).AsBool && Game.CallBuiltin("object_get_physics", i).AsBool) return true;
        return false;
    }

    // A small sprite with a real collision box, for the collision checks: a big one would reach the base too.
    private static string? SolidSprite()
    {
        for (int i = 0; i < 200; i++)
        {
            if (!Game.CallBuiltin("sprite_exists", i).AsBool) continue;
            double w = Game.CallBuiltin("sprite_get_bbox_right", i).AsReal - Game.CallBuiltin("sprite_get_bbox_left", i).AsReal;
            double h = Game.CallBuiltin("sprite_get_bbox_bottom", i).AsReal - Game.CallBuiltin("sprite_get_bbox_top", i).AsReal;
            if (w >= 4 && h >= 4 && w <= 128 && h <= 128) return Game.CallBuiltin("sprite_get_name", i).ToString();
        }
        return null;
    }

    private void Start()
    {
        // A reloaded probe: the previous run's instances must be gone, its objects kept.
        int before = Index(Game.CallBuiltin("asset_get_index", BaseName));
        bool reloaded = before >= 0;
        if (reloaded)
        {
            Expect(Game.CallBuiltin("instance_number", before).AsReal == 0,
                   $"the previous run's instances were destroyed on unload ({Game.CallBuiltin("instance_number", before).AsReal} left)");
        }

        string? sprite = SolidSprite();
        Expect(sprite != null, "a sprite with a collision box");

        _base = ObjectTypes.Define(BaseName, sprite: sprite)
            .On(GameEvent.Create, e =>
            {
                e.Self.Set("probe_created", 1);
                e.Self.Set("probe_steps", 0);
                e.Self.Set("probe_alarm", 0);
                e.Self.Set("probe_user", 0);
                Game.CallBuiltinAs(e.Self, "alarm_set", 0, 3);
            })
            .On(GameEvent.Step, e =>
            {
                double steps = e.Self.Get("probe_steps").AsReal + 1;
                e.Self.Set("probe_steps", steps);
                // User event 0, the way event_user(0) runs it.
                if (steps == 5) Game.CallBuiltinAs(e.Self, "event_perform", 7, 10);
            })
            .On(GameEvent.Alarm(0), e => e.Self.Set("probe_alarm", e.Self.Get("probe_alarm").AsReal + 1))
            .On(GameEvent.User(0), e => e.Self.Set("probe_user", e.Self.Get("probe_user").AsReal + 1))
            .On(GameEvent.DrawGui, _ => _drawGui++);

        // Sprites are not inherited: the child needs its own to collide.
        _child = ObjectTypes.Define(ChildName, parent: BaseName, sprite: sprite)
            .On(GameEvent.Create, e =>
            {
                bool ran = e.CallInherited();
                e.Self.Set("probe_child_created", ran ? 1 : -1);
            });

        _collider = ObjectTypes.Define(ColliderName, sprite: sprite, visible: false)
            .On(GameEvent.Collision(_base.Object), e =>
            {
                _collisions++;
                // `other` is the instance collided with: the child, never the base far away.
                e.Self.Set("probe_hit", e.Other.Get("object_index"));
            })
            .On(GameEvent.Destroy, _ => _destroyed++)
            .On(GameEvent.CleanUp, _ => _cleanedUp++);

        if (reloaded) Expect(_base.Index == before, $"redefining {BaseName} gave back object {before} (got {_base.Index})");

        // In the frame of the definitions: ObjectType.Create makes them take
        // effect first, or these instances would never collide.
        Spawn();
        _startFrame = _frame;
        Log.Info($"{(reloaded ? "reloaded: " : "")}defined {_base}, {_child}, {_collider} (sprite {sprite}); spawned one of each");
    }

    private void Spawn()
    {
        _b = _base!.Create(Far - 5000, Far);
        _c = _child!.Create(Far, Far);
        _k = _collider!.Create(Far, Far);
    }

    private void Check()
    {
        int b = _base!.Index, c = _child!.Index;
        Expect(Game.CallBuiltin("object_exists", c).AsBool, "object_exists(child)");
        Expect(Game.CallBuiltin("object_get_name", c).ToString() == ChildName, "object_get_name(child)");
        Expect(Index(Game.CallBuiltin("object_get_parent", c)) == b, "object_get_parent(child) == base");
        Expect(Index(Game.CallBuiltin("asset_get_index", BaseName)) == b, "asset_get_index(base)");
        Expect(Game.CallBuiltin("object_is_ancestor", c, b).AsBool, "object_is_ancestor(child, base)");
        Expect(_b.Exists && _c.Exists && _k.Exists, "the instances exist");
        Expect(Index(_c.Get("object_index")) == c, "the child instance's object_index");
        Expect(_base.InstanceCount == 2, $"instance_number(base) counts the child's (got {_base.InstanceCount})");
        Expect(_child.InstanceCount == 1, $"instance_number(child) == 1 (got {_child.InstanceCount})");
        Expect(Found(Game.CallBuiltin("instance_find", b, 1)), "instance_find(base, 1) finds the second (the child's)");

        // Deactivating the base walks its instances the way `with (base)` does: the child's goes too.
        Game.CallBuiltin("instance_deactivate_object", b);
        Expect(!_c.Exists && !_b.Exists, "instance_deactivate_object(base) reaches the child's instance");
        Game.CallBuiltin("instance_activate_object", b);
        Expect(_c.Exists && _b.Exists, "instance_activate_object(base) brings both back");

        Expect(_b.Get("probe_created").AsReal == 1, "base Create ran");
        Expect(_c.Get("probe_created").AsReal == 1, "the child's Create ran the base's (CallInherited)");
        Expect(_c.Get("probe_child_created").AsReal == 1, "the child's own Create ran");
        double bs = _b.Get("probe_steps").AsReal, cs = _c.Get("probe_steps").AsReal;
        Expect(bs >= 30, $"base Step ran (steps {bs})");
        Expect(cs >= 30, $"the child's inherited Step ran (steps {cs})");
        Expect(_b.Get("probe_alarm").AsReal == 1 && _c.Get("probe_alarm").AsReal == 1, "Alarm 0 ran once for each");
        Expect(_b.Get("probe_user").AsReal == 1 && _c.Get("probe_user").AsReal == 1, "User event 0 ran once for each");
        Expect(_drawGui >= 30, $"Draw GUI ran ({_drawGui})");

        // Collisions against the base include its child. In a physics room
        // GameMaker leaves collision events to the physics world, which only
        // reports physics instances: a game built on physics never runs them.
        if (_collisions == 0 && PhysicsGame())
            Log.Warning("collision events skipped: this game's objects use physics, so its rooms report collisions through the physics world only");
        else
        {
            Expect(_collisions >= 30, $"the collider's Collision(base) ran ({_collisions})");
            Expect(Index(_k.Get("probe_hit")) == c, $"the collision's other was the child (got {_k.Get("probe_hit")})");
        }
        var k = _k.Resolve();
        Expect(k != null, "the collider resolves to an instance");
        if (k is { } ki)
        {
            var hit = Game.CallBuiltinAs(ki, "instance_place", Far, Far, b);
            string Box(Instance i) => $"spr {i.Get("sprite_index")} mask {i.Get("mask_index")} at {i.Get("x")},{i.Get("y")} bbox {i.Get("bbox_left")},{i.Get("bbox_top")}-{i.Get("bbox_right")},{i.Get("bbox_bottom")}";
            Expect(Found(hit), $"instance_place(base) at the child finds it (collider {Box(ki)}; child {(_c.Resolve() is {} ci ? Box(ci) : "?")})");
            Expect(Game.CallBuiltinAs(ki, "place_meeting", Far, Far, b).AsBool, "place_meeting(base)");
            Expect(!Game.CallBuiltinAs(ki, "place_meeting", Far + 5000, Far, b).AsBool, "place_meeting(base) elsewhere is false");
        }
        double cx = (_c.Get("bbox_left").AsReal + _c.Get("bbox_right").AsReal) / 2, cy = (_c.Get("bbox_top").AsReal + _c.Get("bbox_bottom").AsReal) / 2;
        Expect(Found(Game.CallBuiltin("collision_point", cx, cy, b, false, false)), "collision_point(base) at the child");
        Expect(!Found(Game.CallBuiltin("collision_point", Far + 5001, Far, b, false, false)), "collision_point(base) elsewhere is noone");

        Game.CallBuiltin("instance_destroy", _k.Id);
    }

    private void Finish()
    {
        _done = true;
        Expect(_destroyed == 1 && _cleanedUp == 1, $"Destroy and Clean Up ran once ({_destroyed}, {_cleanedUp})");
        Expect(!_k.Exists, "the collider is gone");
        Log.Info(_failures.Count == 0
            ? $"PASSED: {BaseName} = {_base!.Index}, {ChildName} = {_child!.Index}, {ColliderName} = {_collider!.Index}"
            : $"FAILED: {string.Join("; ", _failures)}");
    }
}

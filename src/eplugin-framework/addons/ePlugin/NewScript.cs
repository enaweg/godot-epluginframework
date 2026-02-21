using Godot;
using System;

public partial class NewScript : Node
{
    public override void _Ready()
    {
        GD.Print("NewScript initialized");
        base._Ready();
    }
}

// The cast. Hero is the persistent party state; enemies are a class hierarchy with virtual
// stats/behavior (each battle spawns one); the NPC implements an interface — the natural C# shape
// for "things the player can talk to" — with its dialogue as a string[] table.
using Koh.GameBoy.Framework;

namespace Koh.Samples.GbJrpg;

class Hero
{
    public int X = 1;
    public int Y = 1;
    public int MaxHp = 20;
    public int Hp = 20;
    public int Attack = 5;
    public int Level = 1;
    public int Exp;

    public void GainExp(int amount)
    {
        Exp += amount;
        if (Exp >= Level * 10)
        {
            Exp = 0;
            Level++;
            MaxHp += 5;
            Attack += 2;
            Hp = MaxHp;
        }
    }
}

abstract class Enemy
{
    public int Hp;

    public abstract string Name { get; }
    public abstract byte Tile { get; }
    public abstract int MaxHp { get; }
    public abstract int Attack { get; }
    public abstract int ExpReward { get; }

    /// <summary>Damage dealt this turn — subclasses flavor it.</summary>
    public virtual int RollDamage() => (byte)(Attack + Rng.Next(3));
}

sealed class Slime : Enemy
{
    public override string Name => "SLIME";
    public override byte Tile => Assets.SlimeTile;
    public override int MaxHp => 8;
    public override int Attack => 2;
    public override int ExpReward => 5;
}

sealed class Bat : Enemy
{
    public override string Name => "BAT";
    public override byte Tile => Assets.BatTile;
    public override int MaxHp => 6;
    public override int Attack => 4;
    public override int ExpReward => 7;

    // Erratic: sometimes misses entirely, sometimes bites hard.
    public override int RollDamage() => Rng.Chance(64) ? 0 : (byte)(Attack + Rng.Next(5));
}

/// <summary>Anything the hero can face and press A on.</summary>
interface IInteractable
{
    int X { get; }
    int Y { get; }
    void Interact();
}

sealed class Villager : IInteractable
{
    private static readonly string[] Lines =
    {
        "WELCOME TRAVELER!",
        "MONSTERS ROAM THE",
        "GRASS. LEVEL UP!",
    };

    public int X => 5;
    public int Y => 1;

    // The natural C# shape for "do this when the dialogue closes": hand the scene a callback.
    public void Interact() =>
        Game.ChangeScene(new DialogueScene(Lines, () => Game.ChangeScene(new OverworldScene())));
}

using UnityEngine;

public interface ILockable
{ 
        Transform Transform { get; } 
        bool IsAlive { get; }
}
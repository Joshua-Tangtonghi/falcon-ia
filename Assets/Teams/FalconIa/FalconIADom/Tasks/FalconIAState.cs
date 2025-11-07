using DoNotModify;
using Teams.ExampleTeam;
using UnityEngine;

public abstract class FalconAIState : StateMachineBehaviour
{
    protected FalconIADom controller;
    protected SpaceShipView _SpaceShip => controller?._SpaceShip;
    protected GameData _GameData => controller?._GameData;
    protected FalconIADom GetController(Animator animator)
    {
        if (controller == null)
            controller = animator.GetComponentInParent<FalconIADom>();
        return controller;
    }
}

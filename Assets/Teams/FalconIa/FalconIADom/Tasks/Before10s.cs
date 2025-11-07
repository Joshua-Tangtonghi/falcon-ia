using DoNotModify;
using System.Runtime.Remoting.Messaging;
using UnityEngine;
using UnityEngine.AI;
public class Before10s : FalconAIState
{
    // Called when a transition starts and the state machine starts to evaluate this state
    override public void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
    {
    }

    // Called on each Update frame between OnStateEnter and OnStateExit callbacks
    override public void OnStateUpdate(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
    {
        var ctrl = GetController(animator);
        if (ctrl == null) return;
    }

    // Called when a transition ends and the state machine finishes evaluating this state
    override public void OnStateExit(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
    {

    }
}

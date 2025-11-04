using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;

public class SeekStateBehaviour : StateMachineBehaviour
{
	public string targetName;
	public string distanceVariable;

	private NavMeshAgent _navMeshAgent = null;
	private GameObject _target = null;

	void UpdateDistanceVariable(Animator animator)
	{
		if (_target == null && _navMeshAgent == null)
			return;
		float distance = (_navMeshAgent.transform.position - _target.transform.position).magnitude;
		animator.SetFloat(distanceVariable, distance);
	}

	// Called when a transition starts and the state machine starts to evaluate this state
	override public void OnStateEnter(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
	{
		if(_navMeshAgent == null) {
			_navMeshAgent = animator.gameObject.GetComponent<NavMeshAgent>();
        }
		if(_target == null) {
			_target = GameObject.Find(targetName);
        }
		UpdateDistanceVariable(animator);
	}

	// Called on each Update frame between OnStateEnter and OnStateExit callbacks
	override public void OnStateUpdate(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
	{
		if (_target == null || _navMeshAgent == null)
			return;
		_navMeshAgent.SetDestination(_target.transform.position);
		UpdateDistanceVariable(animator);
	}

	// Called when a transition ends and the state machine finishes evaluating this state
	override public void OnStateExit(Animator animator, AnimatorStateInfo stateInfo, int layerIndex)
	{
	    
	}
}

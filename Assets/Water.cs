using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(BoxCollider2D))]
public class Water : MonoBehaviour
{
    private readonly HashSet<fishFood> food = new HashSet<fishFood>();

    public BoxCollider2D Area { get; private set; }

    private void Awake()
    {
        Area = GetComponent<BoxCollider2D>();
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (other.TryGetComponent(out fishFood pellet) && pellet.FloatIn(this))
            food.Add(pellet);
    }

    private void OnTriggerExit2D(Collider2D other)
    {
        if (other.TryGetComponent(out fishFood pellet))
            pellet.Leave(this);
    }

    public void RemoveFood(fishFood pellet)
    {
        food.Remove(pellet);
    }

    public fishFood FindClosestFood(Vector2 position)
    {
        fishFood closest = null;
        float nearestDistance = float.PositiveInfinity;
        foreach (fishFood pellet in food)
        {
            float distance = ((Vector2)pellet.transform.position - position).sqrMagnitude;
            if (distance < nearestDistance)
            {
                closest = pellet;
                nearestDistance = distance;
            }
        }
        return closest;
    }
}
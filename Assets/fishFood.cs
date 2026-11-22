using UnityEngine;

[RequireComponent(typeof(Rigidbody2D))]
public class fishFood : MonoBehaviour
{
    private Rigidbody2D body;
    private Water water;
    private bool consumed;

    private void Awake()
    {
        body = GetComponent<Rigidbody2D>();
        Destroy(gameObject, 90f);
    }

    public bool FloatIn(Water tank)
    {
        if (consumed)
            return false;

        water = tank;
        body.gravityScale = 0f;
        body.linearVelocity = Vector2.zero;
        body.angularVelocity = 0f;
        return true;
    }

    public void Leave(Water tank)
    {
        if (water != tank)
            return;

        tank.RemoveFood(this);
        water = null;
    }

    public bool TryConsume()
    {
        if (consumed)
            return false;

        consumed = true;
        gameObject.SetActive(false);
        Destroy(gameObject);
        return true;
    }

    private void OnDisable()
    {
        if (water != null)
            Leave(water);
    }
}
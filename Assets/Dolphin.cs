using System.Collections;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D), typeof(SpriteRenderer))]
public class Dolphin : MonoBehaviour
{
    [SerializeField, Min(0.1f)] private float moveSpeed = 30f;
    [SerializeField] private GameObject dissapDolphPrefab;
    [SerializeField] private Vector2 dissapDolphStartPosition;
    [SerializeField] private Vector2 dissapDolphEndPosition;

    private Rigidbody2D body;
    private Fish target;
    private Vector2 startPosition;
    private bool returning;

    private void Awake()
    {
        body = GetComponent<Rigidbody2D>();
        body.gravityScale = 0f;
        startPosition = body.position;
    }

    public void Hunt(Fish fish)
    {
        target = fish;
        if (target == null)
        {
            GetComponent<SpriteRenderer>().enabled = false;
            body.simulated = false;
            StartCoroutine(ShowDisappointedDolphin());
        }
    }

    private void FixedUpdate()
    {
        if (!body.simulated)
            return;

        if (target == null || !target.IsSwimming)
            returning = true;

        Vector2 destination = returning ? startPosition : (Vector2)target.transform.position;
        Vector2 offset = destination - body.position;
        if (offset.sqrMagnitude < 0.1f * 0.1f)
        {
            if (returning)
                Destroy(gameObject);
            else
                CatchFish();
            return;
        }

        body.MovePosition(Vector2.MoveTowards(body.position, destination, moveSpeed * Time.fixedDeltaTime));
        body.MoveRotation(Mathf.Atan2(offset.y, offset.x) * Mathf.Rad2Deg);
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (!returning && target != null && other.gameObject == target.gameObject)
            CatchFish();
    }

    private void CatchFish()
    {
        target.Eaten();
        returning = true;
    }

    private IEnumerator ShowDisappointedDolphin()
    {
        GameObject disappointed = Instantiate(dissapDolphPrefab, dissapDolphStartPosition, Quaternion.identity);
        disappointed.transform.SetParent(transform, true);
        yield return SwimTo(disappointed.transform, dissapDolphEndPosition);
        yield return new WaitForSeconds(2f);
        yield return SwimTo(disappointed.transform, dissapDolphStartPosition);
        Destroy(gameObject);
    }

    private IEnumerator SwimTo(Transform dolphin, Vector2 destination)
    {
        while (Vector2.Distance(dolphin.position, destination) > 0.1f)
        {
            dolphin.position = Vector2.MoveTowards(dolphin.position, destination, moveSpeed * Time.deltaTime);
            yield return null;
        }
    }
}
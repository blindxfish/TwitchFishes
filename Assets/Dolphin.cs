using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class Dolphin : MonoBehaviour
{
    public Transform target;
    private Rigidbody2D rb;
    private Vector2 startPosition;
    public float moveSpeed = 30f; // Adjusted to match Fish speed
    private bool isReturning = false;
    private SpriteRenderer spr;
    public GameObject dissapDolphPrefab; // Prefab of the disappointed dolphin
    public Vector2 dissapDolphStartPosition;
    public Vector2 dissapDolphEndPosition;

    void Start()
    {
        rb = GetComponent<Rigidbody2D>();
        if (rb == null)
        {
            rb = gameObject.AddComponent<Rigidbody2D>();
        }
        rb.gravityScale = 0; // Ensure the Dolphin doesn't fall due to gravity

        startPosition = transform.position; // Store the initial position

        TwitchChat twitchChat = FindObjectOfType<TwitchChat>();
        if (twitchChat != null && twitchChat.userObjects != null)
        {
            target = GetRandomValidGameObject(twitchChat.userObjects)?.transform;
        }

        if (target == null)
        {
            Debug.Log("No target found. Spawning dissapDolph.");
            SpawnDissapDolph();
           
        }
        else
        {
            spr = GetComponent<SpriteRenderer>();
            if (spr == null)
            {
                spr = gameObject.AddComponent<SpriteRenderer>();
            }
        }
    }

    private GameObject GetRandomValidGameObject(Dictionary<string, GameObject> dictionary)
    {
        if (dictionary == null || dictionary.Count == 0)
        {
            Debug.Log("The dictionary is empty.");
            return null;
        }

        List<GameObject> values = new List<GameObject>(dictionary.Values);
        values.RemoveAll(item => item == null); // Remove any destroyed objects

        if (values.Count == 0)
        {
            Debug.Log("No valid GameObjects found in the dictionary.");
            return null;
        }

        System.Random random = new System.Random();
        int randomIndex = random.Next(values.Count);
        return values[randomIndex];
    }

    void Update()
    {
        if (!isReturning && target != null)
        {
            MoveTowardsTarget(target.position);
        }
        else if (isReturning)
        {
            MoveTowardsTarget(startPosition);
            if (Vector2.Distance(transform.position, startPosition) < 0.1f)
            {
                Destroy(gameObject); // Destroy the dolphin after reaching the initial position
            }
        }

        if (target != null || isReturning)
        {
            Vector2 direction = isReturning ? (startPosition - (Vector2)transform.position) : ((Vector2)target.position - (Vector2)transform.position);
            float angle = Mathf.Atan2(direction.y, direction.x) * Mathf.Rad2Deg;
            transform.rotation = Quaternion.Euler(new Vector3(0, 0, angle));
        }
    }

    void MoveTowardsTarget(Vector2 targetPosition)
    {
        Vector2 newPosition = Vector2.MoveTowards(transform.position, targetPosition, moveSpeed * Time.deltaTime);
        rb.MovePosition(newPosition);

        // Check for collision manually since OnTriggerEnter2D might not be reliable
        if (!isReturning && Vector2.Distance(transform.position, targetPosition) < 0.1f)
        {
            Fish targetFish = target.GetComponent<Fish>();
            if (targetFish != null)
            {
                targetFish.Eaten(); // Call the Eaten function on the Fish script
                isReturning = true;
            }
        }
    }

    void OnTriggerEnter2D(Collider2D collision)
    {
        if (collision.transform == target)
        {
            StopAllCoroutines();
            Fish fish = collision.GetComponent<Fish>();
            if (fish != null)
            {
                fish.Eaten(); // Call the Eaten function on the Fish script
            }
            isReturning = true;
        }
    }

    void SpawnDissapDolph()
    {
        GameObject dissapDolph = Instantiate(dissapDolphPrefab, dissapDolphStartPosition, Quaternion.identity);
        StartCoroutine(MoveDissapDolph(dissapDolph));
    }

    IEnumerator MoveDissapDolph(GameObject dissapDolph)
    {
        Vector2 startPos = dissapDolphStartPosition;
        Vector2 endPos = dissapDolphEndPosition;

        while (true)
        {
            // Move to end position
            while (Vector2.Distance(dissapDolph.transform.position, endPos) > 0.1f)
            {
                Vector2 newPosition = Vector2.MoveTowards(dissapDolph.transform.position, endPos, moveSpeed * Time.deltaTime);
                dissapDolph.transform.position = newPosition;
                yield return null;
            }

            // Wait for 2 seconds
            yield return new WaitForSeconds(2f);

            // Move back to start position
            while (Vector2.Distance(dissapDolph.transform.position, startPos) > 0.1f)
            {
                Vector2 newPosition = Vector2.MoveTowards(dissapDolph.transform.position, startPos, moveSpeed * Time.deltaTime);
                dissapDolph.transform.position = newPosition;
                yield return null;
            }

            // Wait for 2 seconds
            yield return new WaitForSeconds(2f);
            Destroy(dissapDolph.gameObject);
            Destroy(gameObject); // Destroy the original dolphin as there's no target
        }
    }
}

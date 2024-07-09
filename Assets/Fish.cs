using System.Collections;
using UnityEngine;

using TMPro;  // Added this line to include TextMeshPro

public class Fish : MonoBehaviour
{
    public Rigidbody2D rb;
    public BoxCollider2D water;
    public BoxCollider2D spawnArea;
    public TMP_Text playerNameText;  // Changed this from TextMesh to TMP_Text
    public string playerName;

    private bool isMovingTowardsFood = false;  // Flag to manage food movement
    private SpriteRenderer spr;
    public bool inWater = false;
    private Vector2 direction;
    private float directionChangeInterval = 2f;
    private float moveSpeed = 30f;
    public GameObject scorePrefab; // Prefab for displaying score effects
    void Start()
    {
        spr = this.gameObject.GetComponent<SpriteRenderer>();
        rb = GetComponent<Rigidbody2D>();
        // Set the fish at a random position within the spawn area
        transform.position = new Vector2(
            Random.Range(spawnArea.bounds.min.x, spawnArea.bounds.max.x),
            Random.Range(spawnArea.bounds.min.y, spawnArea.bounds.max.y)
        );
    }

    void Update()
    {
        if (inWater && !isMovingTowardsFood)
        {
            GameObject closestFood = GetClosestFood();
            if (closestFood != null)
            {
                StartCoroutine(MoveTowardsFood(closestFood.transform.position));
            }
            else
            {
                Swim();
                ClampPositionToWaterBounds();
            }
        }
    }

    GameObject GetClosestFood()
    {
        float minDistance = float.MaxValue;
        GameObject closest = null;
        foreach (GameObject food in Water.foodObjects)
        {
            float distance = Vector2.Distance(transform.position, food.transform.position);
            if (distance < minDistance)
            {
                minDistance = distance;
                closest = food;
            }
        }
        return closest;
    }
    void Swim()
    {
        // Choose a new direction at regular intervals
        directionChangeInterval -= Time.deltaTime;
        if (directionChangeInterval <= 0)
        {
            // Choose a random direction
            direction = new Vector2(Random.Range(-1f, 1f), Random.Range(-1f, 1f));
            directionChangeInterval = Random.Range(2f, 5f);
        }
        
        // Flip the fish's rotation based on the direction of movement
        if (direction.x > 0)
        {
            spr.flipX = false;
        }
        else if (direction.x < 0)
        {
            spr.flipX = true;
        }

        // Move in the chosen direction
        rb.velocity = direction * moveSpeed * Time.deltaTime;
    }


    void ClampPositionToWaterBounds()
    {
        // Prevent the fish from leaving the water
        Vector3 position = transform.position;
        position.x = Mathf.Clamp(position.x, water.bounds.min.x, water.bounds.max.x);
        position.y = Mathf.Clamp(position.y, water.bounds.min.y, water.bounds.max.y);
        transform.position = position;
    }

    void OnTriggerEnter2D(Collider2D other)
    {
        // Check if the fish entered the water
        if (other == water)
        {
            inWater = true;
        }
        if (other.tag == "FishFood")
        {
            Debug.Log($" {playerName} Eat the food");
            EatFood(other.gameObject);
        }
    }

    void OnTriggerExit2D(Collider2D other)
    {
        // Check if the fish left the water
        if (other == water)
        {
            inWater = false;
        }
    }



    IEnumerator MoveTowardsFood(Vector2 foodPosition)
    {
        isMovingTowardsFood = true;
        while (Vector2.Distance(transform.position, foodPosition) > 0.1f)
        {
            Vector2 newPosition = Vector2.MoveTowards(transform.position, foodPosition, moveSpeed * Time.deltaTime);
            if ((newPosition - (Vector2)transform.position).x > 0) { spr.flipX = false; }
            else if ((newPosition - (Vector2)transform.position).x < 0) { spr.flipX = true; }
            rb.MovePosition(newPosition);
            yield return null;
        }
        isMovingTowardsFood = false;  // Reset the flag when movement is done
    }

    private void EatFood(GameObject food)
    {
        DisplayScoreEffect();
        Destroy(food);
        FindObjectOfType<TwitchChat>().UpdateScore(playerName); // Assuming one instance of TwitchChat

    }

    public void Eaten()
    {
        FindObjectOfType<TwitchChat>().LostScore(gameObject); // Assuming one instance of TwitchChat
        this.gameObject.GetComponent<SpriteRenderer>().enabled = false;
        DeadScoreScoreEffect();
    }

    private void DisplayScoreEffect()
    {
        GameObject scoreEffect = Instantiate(scorePrefab, transform.position, Quaternion.identity);
        scoreEffect.GetComponentInChildren<TMP_Text>().text = playerName + " +1";
        StartCoroutine(FloatAndFade(scoreEffect));
    }

    private void DeadScoreScoreEffect()
    {
        GameObject scoreEffect = Instantiate(scorePrefab, transform.position, Quaternion.identity);
        scoreEffect.GetComponentInChildren<TMP_Text>().text = playerName + " is EATEN!!!";
        StartCoroutine(FloatAndFade(scoreEffect));
    }

    private IEnumerator FloatAndFade(GameObject scoreEffect)
    {
        float duration = 2.0f; // Duration of the effect in seconds
        float timer = 0;

        Vector3 startPosition = scoreEffect.transform.position;
        Vector3 endPosition = startPosition + new Vector3(0, 1, 0); // Move 1 unit up

        while (timer < duration)
        {
            float t = timer / duration;
            scoreEffect.transform.position = Vector3.Lerp(startPosition, endPosition, t);
            // Fade effect can be added here if needed
            timer += Time.deltaTime;
            yield return null;
        }

        Destroy(scoreEffect);
    }
}

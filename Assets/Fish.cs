using System.Collections;
using TMPro;
using UnityEngine;

[RequireComponent(typeof(Rigidbody2D), typeof(SpriteRenderer), typeof(BoxCollider2D))]
public class Fish : MonoBehaviour
{
    [SerializeField] private TMP_Text playerNameText;
    [SerializeField] private GameObject scorePrefab;
    [SerializeField, Min(0.1f)] private float swimSpeed = 2.6f;
    [SerializeField, Min(0.1f)] private float chaseSpeed = 7f;
    [SerializeField, Min(0.1f)] private float acceleration = 7f;

    private static readonly int WaveSpeed = Shader.PropertyToID("_WaveSpeed");
    private static readonly int RandomSeed = Shader.PropertyToID("_RandomSeed");
    private Rigidbody2D body;
    private SpriteRenderer sprite;
    private BoxCollider2D hitbox;
    private TwitchChat owner;
    private Water water;
    private BoxCollider2D spawnArea;
    private Vector2 swimDirection;
    private float nextTurn;
    private float pace;
    private float fallGravity;

    public string Login { get; private set; }
    public string DisplayName { get; private set; }
    public bool IsSwimming { get; private set; }
    public bool IsRespawning { get; private set; }

    private void Awake()
    {
        body = GetComponent<Rigidbody2D>();
        sprite = GetComponent<SpriteRenderer>();
        hitbox = GetComponent<BoxCollider2D>();
        fallGravity = body.gravityScale;
    }

    public void Initialize(TwitchChat manager, Water tank, BoxCollider2D spawn, string login, string displayName, Sprite appearance)
    {
        owner = manager;
        water = tank;
        spawnArea = spawn;
        Login = login;
        SetDisplayName(displayName);
        if (appearance != null)
            sprite.sprite = appearance;

        var wave = new MaterialPropertyBlock();
        sprite.GetPropertyBlock(wave);
        wave.SetFloat(WaveSpeed, Random.Range(4.5f, 7f));
        wave.SetFloat(RandomSeed, Random.Range(0f, 20f));
        sprite.SetPropertyBlock(wave);
        pace = Random.Range(0.75f, 1.25f);
        DropIntoTank();
    }

    public void SetDisplayName(string displayName)
    {
        DisplayName = displayName;
        gameObject.name = displayName;
        playerNameText.text = displayName;
    }

    private void DropIntoTank()
    {
        Bounds bounds = spawnArea.bounds;
        body.position = new Vector2(Random.Range(bounds.min.x, bounds.max.x), Random.Range(bounds.min.y, bounds.max.y));
        body.linearVelocity = Vector2.zero;
        body.gravityScale = fallGravity;
        body.simulated = true;
        IsSwimming = false;
        IsRespawning = false;
        sprite.enabled = true;
        playerNameText.enabled = true;
    }

    private void FixedUpdate()
    {
        if (!IsSwimming)
            return;

        Vector2 position = body.position;
        Bounds bounds = water.Area.bounds;
        var lower = (Vector2)bounds.min + new Vector2(0.7f, 0.55f);
        var upper = (Vector2)bounds.max - new Vector2(0.7f, 0.55f);

        if (Time.fixedTime >= nextTurn)
        {
            swimDirection = new Vector2(Random.value < 0.5f ? -1f : 1f, Random.Range(-0.32f, 0.32f)).normalized;
            nextTurn = Time.fixedTime + Random.Range(1.8f, 4.5f);
        }

        if (position.x <= lower.x || position.x >= upper.x)
            swimDirection.x = Mathf.Abs(swimDirection.x) * (position.x <= lower.x ? 1f : -1f);
        if (position.y <= lower.y || position.y >= upper.y)
            swimDirection.y = Mathf.Abs(swimDirection.y) * (position.y <= lower.y ? 1f : -1f);

        fishFood food = water.FindClosestFood(position);
        Vector2 velocity = swimDirection * (swimSpeed * pace);
        if (food != null)
        {
            Vector2 offset = (Vector2)food.transform.position - position;
            velocity = Vector2.ClampMagnitude(offset / Time.fixedDeltaTime, chaseSpeed * pace);
        }

        Vector2 nextVelocity = Vector2.MoveTowards(body.linearVelocity, velocity, acceleration * Time.fixedDeltaTime);
        Vector2 nextPosition = position + nextVelocity * Time.fixedDeltaTime;
        nextPosition.x = Mathf.Clamp(nextPosition.x, lower.x, upper.x);
        nextPosition.y = Mathf.Clamp(nextPosition.y, lower.y, upper.y);
        body.linearVelocity = (nextPosition - position) / Time.fixedDeltaTime;

        if (Mathf.Abs(nextVelocity.x) > 0.12f)
            sprite.flipX = nextVelocity.x < 0f;
    }

    private void OnTriggerEnter2D(Collider2D other)
    {
        if (IsRespawning || water == null)
            return;

        if (other == water.Area)
        {
            IsSwimming = true;
            body.gravityScale = 0f;
            body.linearVelocity = Vector2.zero;
            nextTurn = Time.fixedTime;
        }
        else if (other.TryGetComponent(out fishFood food) && food.TryConsume())
        {
            owner.AddScore(this, 1);
            ShowScore("+1", new Color(0.48f, 1f, 0.72f));
        }
    }

    public void Eaten()
    {
        if (IsRespawning)
            return;

        IsRespawning = true;
        IsSwimming = false;
        body.simulated = false;
        sprite.enabled = false;
        playerNameText.enabled = false;
        owner.AddScore(this, -25);
        ShowScore("−25", new Color(1f, 0.51f, 0.43f));
        StartCoroutine(Respawn());
    }

    private IEnumerator Respawn()
    {
        yield return new WaitForSeconds(3f);
        DropIntoTank();
    }

    private void ShowScore(string change, Color color)
    {
        var position = transform.position + new Vector3(0f, hitbox.size.y * 0.5f + 0.2f, -0.1f);
        var effect = Instantiate(scorePrefab, position, Quaternion.identity).GetComponent<TMP_Text>();
        effect.richText = false;
        effect.text = $"{DisplayName} {change}";
        effect.color = color;
        Destroy(effect.gameObject, 2f);
        StartCoroutine(FloatAndFade(effect, position, color));
    }

    private IEnumerator FloatAndFade(TMP_Text effect, Vector3 origin, Color color)
    {
        const float duration = 1.8f;
        for (float time = 0f; time < duration && effect != null; time += Time.deltaTime)
        {
            float progress = time / duration;
            effect.transform.position = origin + Vector3.up * (1.1f * progress);
            color.a = 1f - progress * progress;
            effect.color = color;
            yield return null;
        }
        if (effect != null)
            Destroy(effect.gameObject);
    }
}
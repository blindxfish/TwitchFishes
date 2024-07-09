using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Windows;
using WebSocketSharp;
using ErrorEventArgs = WebSocketSharp.ErrorEventArgs;
using File = System.IO.File;

public class TwitchChat : MonoBehaviour
{
    private WebSocket ws;

    // Dictionary to hold users and the time when their object should be removed
    private Dictionary<string, float> users = new Dictionary<string, float>();
    public Dictionary<string, GameObject> userObjects = new Dictionary<string, GameObject>();
    private Dictionary<string, int> scores = new Dictionary<string, int>(); // Track scores per username

    private ConcurrentQueue<Action> actions = new ConcurrentQueue<Action>();
    private Dictionary<string, string> configValues = new Dictionary<string, string>();
    public GameObject alertCanvas;

    public BoxCollider2D spawnArea;
    public BoxCollider2D dolphinSpawnArea;

    public GameObject topFishCanvas; // Assign this in the Unity Editor
    private TMP_Text topFishText; // This will be the TextMeshProUGUI component


    public GameObject fishPrefab;
    public GameObject foodPrefab;
    public GameObject dolphinPrefab;
    public GameObject dissapDolphPrefab; // Prefab for the disappointed dolphin

    public Sprite[] fishSprites;

    private float lastDolphinTime = 0f; // Timestamp for the last dolphin action
    private const float dolphinCooldown = 60f; // Cooldown time in seconds

    void Start()
    {

        string path = Application.streamingAssetsPath + "/config.txt";
        // Assign the TMP_Text component from the child of topFishCanvas
        topFishText = topFishCanvas.GetComponentInChildren<TMP_Text>();

        // Initially hide the topFishCanvas
        topFishCanvas.SetActive(false);
        if (File.Exists(path))
        {
            string[] configData = File.ReadAllLines(path);

            foreach (string line in configData)
            {
                if (line.Contains("<placeholder>"))
                {
                    // Placeholder found. Activate canvas.
                    alertCanvas.SetActive(true);
                    return;
                }
                else
                {
                    alertCanvas.SetActive(false);
                    LoadConfig();
                }
            }
            // Placeholder not found. Deactivate canvas.
            alertCanvas.SetActive(false);
        }
        else
        {
            // File does not exist. Activate canvas.
            alertCanvas.SetActive(true);

        }
        Application.runInBackground = true;
        //     fishSprites = Resources.LoadAll<Sprite>("Fishes");

        ws = new WebSocket("wss://irc-ws.chat.twitch.tv:443");

        ws.OnOpen += OnOpenHandler;
        ws.OnMessage += OnMessageHandler;
        ws.OnClose += OnCloseHandler;
        ws.OnError += OnErrorHandler;

        ws.Connect();

        StartCoroutine(CheckAndRemoveObjects());
    }


    void Update()
    {
        while (actions.TryDequeue(out Action action))
        {
            action.Invoke();
        }
    }

    void LoadConfig()
    {
        string path = Application.streamingAssetsPath + "/config.txt";
        try
        {
            using (StreamReader reader = new StreamReader(path))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] parts = line.Split(':');
                    if (parts.Length == 2)
                    {
                        configValues[parts[0]] = parts[1];
                    }
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError("Could not load config file: " + e.Message);
        }
    }

    private void OnOpenHandler(object sender, System.EventArgs e)
    {
        Debug.Log("WebSocket connected!");

        ws.Send("CAP REQ :twitch.tv/tags twitch.tv/commands twitch.tv/membership");
        ws.Send($"PASS oauth:{configValues["oauth"]}");
        ws.Send($"NICK {configValues["nick"]}");
        ws.Send($"JOIN #{configValues["channel"]}");

    }

    private void OnMessageHandler(object sender, MessageEventArgs e)
    {
        Debug.Log(e.Data);
        // Check if the message is a PING
        if (e.Data.StartsWith("PING"))
        {
            // Send a PONG back with the message received from the PING
            ws.Send($"PONG :{e.Data.Substring(e.Data.IndexOf(":") + 1)}");
        }

        // Check if the message is a PRIVMSG
        if (e.Data.Contains("PRIVMSG"))
        {
            //  Debug.Log(e.Data);
            // Parse the username from the raw message
            string pattern = @"display-name=([^;]+)";
            string foodPatter = @"!food";

            Match match = Regex.Match(e.Data, pattern);
            Match foodMatch = Regex.Match(e.Data, foodPatter);

            string username = string.Empty;

            if (match.Success)
            {
                // The username is in the first group of the match
                username = match.Groups[1].Value;
            }
            else
            {
                username = "??X??X?";
            }

            if (foodMatch.Success)
            {
                //     Debug.Log("Food requested!");
                actions.Enqueue(makeFood); // Enqueue the action to make food
            }

            HandleMessage(username);

            // Check for !topFish command
            if (e.Data.Contains("!topFish"))
            {
                actions.Enqueue(DisplayTopFish);
            }
            if (e.Data.Contains("!dolphin"))
            {
                actions.Enqueue(() =>
                {
                    if (Time.time - lastDolphinTime >= dolphinCooldown)
                    {
                        doDolphin();
                        lastDolphinTime = Time.time;
                    }
                    else
                    {
                        ShowDisappointedDolphin();
                    }
                });
            }

        }
        else
        {
            // Debug.Log("Received message in unexpected format: " + e.Data);
        }
    }


    private void DisplayTopFish()
    {
        var topScorers = scores.OrderByDescending(pair => pair.Value).Take(5).ToList();
        string topScoresMessage = "Top Fishes:\n";
        foreach (var scorer in topScorers)
        {
            topScoresMessage += $"{scorer.Key}: {scorer.Value}\n";
        }

        // Update the text component and show the canvas
        topFishText.text = topScoresMessage;
        topFishCanvas.SetActive(true);

        // Start coroutine to hide the canvas after 5 seconds
        StartCoroutine(HideTopFishCanvas());
    }
    private IEnumerator HideTopFishCanvas()
    {
        yield return new WaitForSeconds(5); // Wait for 5 seconds
        topFishCanvas.SetActive(false); // Then hide the canvas
    }
    public void UpdateScore(string username)
    {
        if (!scores.ContainsKey(username))
            scores[username] = 0;
        scores[username]++;
    }

    public void LostScore(GameObject fishObjetc)
    {
        Fish fishScript = fishObjetc.GetComponent<Fish>();
        fishScript.inWater = false;
        if (!scores.ContainsKey(fishScript.playerName))
            scores[fishScript.playerName] = 0;
        scores[fishScript.playerName] = scores[fishScript.playerName] - 25;
        StartCoroutine(ReaspawnFish(fishObjetc));
    }

    private void OnCloseHandler(object sender, CloseEventArgs e)
    {
        Debug.Log("WebSocket closed with reason: " + e.Reason);
    }

    private void OnErrorHandler(object sender, ErrorEventArgs e)
    {
        Debug.Log("WebSocket error: " + e.Message);
    }

    void OnDestroy()
    {
        ws.Close();
    }

    private GameObject CreateObjectForUser(string username)
    {
        // Instantiate the fish
        GameObject fishObject = Instantiate(fishPrefab);

        // Get the SpriteRenderer component
        SpriteRenderer spriteRenderer = fishObject.GetComponent<SpriteRenderer>();
        if (spriteRenderer != null)
        {
            // Debug.Log(fishSprites.Length);
            // Choose a random sprite and assign it to the SpriteRenderer
            spriteRenderer.sprite = fishSprites[UnityEngine.Random.Range(0, fishSprites.Length - 1)];
        }
        else
        {
            Debug.LogError("No SpriteRenderer found on " + fishObject.name);
        }

        // Set the name of the fish to the username
        fishObject.name = username;

        // Get the Fish script and set the player name
        Fish fishScript = fishObject.GetComponent<Fish>();
        if (fishScript != null)
        {
            fishScript.playerName = username;
        }
        else
        {
            Debug.LogError("No Fish component found on " + fishObject.name);
        }
        // Find the "name" child and update its TextMeshPro text
        Transform nameTransform = fishObject.transform.Find("name");
        if (nameTransform != null)
        {
            TMPro.TextMeshPro nameText = nameTransform.GetComponent<TMPro.TextMeshPro>();
            if (nameText != null)
            {
                nameText.text = username;
            }
            else
            {
                Debug.LogError("No TextMeshPro component found on " + nameTransform.name);
            }
        }
        else
        {
            Debug.LogError("No child called 'name' found on " + fishObject.name);
        }
        // Set the fish's position, rotation, etc.

        return fishObject;
    }

    private void HandleMessage(string username)
    {
        if (users.ContainsKey(username))
        {
            actions.Enqueue(() => {
                // Reset the time to 15 minutes from now
                users[username] = 900;
                Debug.Log("Time updated to " + users[username]);
            });

        }
        else
        {
            // Instead of directly calling CreateObjectForUser, queue up the action
            actions.Enqueue(() => {
                //    Debug.Log(username);
                // Add the user and instantiate a new object for them
                users.Add(username, 900);
                userObjects.Add(username, CreateObjectForUser(username));
                //   Debug.Log("New fish added and the time set to" + users[username]);
            });
        }
    }

    public void makeFood()
    {
        Vector2 spawnPosition = new Vector2(
        UnityEngine.Random.Range(spawnArea.bounds.min.x, spawnArea.bounds.max.x),
        UnityEngine.Random.Range(spawnArea.bounds.min.y, spawnArea.bounds.max.y)
        );

        // Instantiate the foodPrefab at the calculated position
        GameObject fishFood = Instantiate(foodPrefab, spawnPosition, Quaternion.identity); // Use Quaternion.identity for no rotation

    }

    public void doDolphin()
    {
        Vector2 spawnPosition = new Vector2(
       UnityEngine.Random.Range(dolphinSpawnArea.bounds.min.x, dolphinSpawnArea.bounds.max.x),
       UnityEngine.Random.Range(dolphinSpawnArea.bounds.min.y, dolphinSpawnArea.bounds.max.y)
       );

        // Instantiate the foodPrefab at the calculated position
        GameObject dolphin = Instantiate(dolphinPrefab, spawnPosition, Quaternion.identity); // Use Quaternion.identity for no rotation

    }

    public void ShowDisappointedDolphin()
    {
        Vector2 spawnPosition = new Vector2(-17, -7);
        GameObject dissapDolph = Instantiate(dissapDolphPrefab, spawnPosition, Quaternion.identity);
        StartCoroutine(RemoveDissapDolphAfterDelay(dissapDolph, 3f));
    }

    private IEnumerator RemoveDissapDolphAfterDelay(GameObject dissapDolph, float delay)
    {
        yield return new WaitForSeconds(delay);
        Destroy(dissapDolph);
    }

    IEnumerator ReaspawnFish(GameObject fish)
    {
        yield return new WaitForSeconds(3f);

        Vector2 spawnPosition = new Vector2(
        UnityEngine.Random.Range(spawnArea.bounds.min.x, spawnArea.bounds.max.x),
        UnityEngine.Random.Range(spawnArea.bounds.min.y, spawnArea.bounds.max.y)
        );

        fish.transform.position = spawnPosition;
        fish.GetComponent<SpriteRenderer>().enabled = true;

    }

    public void SimulateChatMessage()
    {
        // Generate a random username with a base of "user" and a random number
        string randomUsername = "user" + UnityEngine.Random.Range(0, 10000);
        HandleMessage(randomUsername);
    }

    private IEnumerator CheckAndRemoveObjects()
    {
        while (true)
        {
            var usersToDecrease = new List<string>(users.Keys);
            var usersToRemove = new List<string>();

            foreach (var user in usersToDecrease)
            {
                // Decrease the time remaining for each user
                users[user]--;

                if (users[user] <= 0)
                {
                    // Time to destroy the user's object
                    Destroy(userObjects[user]);
                    usersToRemove.Add(user);
                }
            }

            // Remove users from dictionaries
            foreach (var user in usersToRemove)
            {
                Debug.Log("User removed" + user);

                users.Remove(user);
                userObjects.Remove(user);
            }

            yield return new WaitForSeconds(1);
        }
    }
}
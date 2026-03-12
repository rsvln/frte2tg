using System.Collections.Concurrent;
using Telegram.Bot;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace frte2tg
{
    public class AITask
    {
        public List<string> ImagePaths { get; set; }
        public long ChatId { get; set; }
        public int MessageId { get; set; }
        public string Camera { get; set; }
        public string EventId { get; set; }
        public DateTime QueuedAt { get; set; }
        public string OriginalCaption { get; set; }
        public string Prompt { get; set; }
    }



    public class AIResponse
    {
        public int people_count { get; set; }
        public string description { get; set; }
        public string timestamp { get; set; }
    }

    public class OllamaResponse
    {
        public string response { get; set; }
    }

    public class AIQueueService
    {
        private readonly ConcurrentQueue<AITask> _queue = new ConcurrentQueue<AITask>();
        private readonly HttpClient httpClient;
        private readonly ITelegramBotClient tgBotClient;
        private readonly string aiApiUrl;
        private readonly SemaphoreSlim semaphore;
        private readonly CancellationTokenSource cts;
        private readonly string aiModel;
        private readonly string humanPrompt;
        private readonly string nonHumanPrompt;
        private readonly int numPredict;
        private readonly double temperature;
        private readonly int resizeToWidth;

        private Task _workerTask;

        public AIQueueService(
            ITelegramBotClient botClient, 
            AISettings aiSettings,
            int maxConcurrentRequests = 2)
        {
            aiApiUrl = aiSettings.url;
            aiModel = aiSettings.model;
            humanPrompt = aiSettings.humanprompt;
            nonHumanPrompt = aiSettings.nonhumanprompt;
            numPredict = aiSettings.numpredict;
            temperature = aiSettings.temperature;
            resizeToWidth = aiSettings.resizetowidth;
            tgBotClient = botClient;
            httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            semaphore = new SemaphoreSlim(2);
            cts = new CancellationTokenSource();            
        }

        public void Start()
        {
            _workerTask = Task.Run(() => ProcessQueueAsync(cts.Token));
            Program.Log("application", "", "", " AI queue service started");
        }

        public void Stop()
        {
            cts.Cancel();
            _workerTask?.Wait();
            Program.Log("ai", "", "", "Queue service stopped");
        }

        public void AddToQueue(AITask task)
        {
            task.QueuedAt = DateTime.Now;
            _queue.Enqueue(task);
            Program.Log("ai", task.EventId, task.Camera, $"Added to queue ({_queue.Count} in queue)");
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (_queue.TryDequeue(out var task))
                    {
                        await semaphore.WaitAsync(cancellationToken);
                        
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                await ProcessTaskAsync(task);
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }, cancellationToken);
                    }
                    else
                    {
                        await Task.Delay(1000, cancellationToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Program.Log("ai", "", "", $"Queue processing error: {ex.Message}");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        private async Task ProcessTaskAsync(AITask task)
        {
            try
            {
                Program.Log("ai", task.EventId, task.Camera, $"Processing {task.ImagePaths.Count} images, queued {(DateTime.Now - task.QueuedAt).TotalSeconds:F1}s ago");

                var descriptions = new List<string>();
                int idx = 1;
                foreach (var path in task.ImagePaths)
                {
                    if (!System.IO.File.Exists(path))
                    {
                        Program.Log("ai", task.EventId, task.Camera, $"Image not found: {path}");
                        idx++;
                        continue;
                    }
                    var desc = await CallAIApiAsync(path, task.Prompt, task.EventId, task.Camera);
                    if (!string.IsNullOrEmpty(desc))
                        descriptions.Add(task.ImagePaths.Count > 1 ? $"📸 {idx}. {desc}" : desc);
                    idx++;
                }

                if (descriptions.Count == 0)
                {
                    Program.Log("ai", task.EventId, task.Camera, "No AI descriptions returned");
                    return;
                }

                await UpdateTelegramMessageAsync(task, string.Join("\n\n", descriptions));
                Program.Log("ai", task.EventId, task.Camera, "Task completed");
            }
            catch (Exception ex)
            {
                Program.Log("ai", task.EventId, task.Camera, $"Task failed: {ex.Message}");
            }
        }

        private async Task<string> CallAIApiAsync(string imagePath, string prompt, string eventId, string camera)
        {
            try
            {
                var imageBytes = await System.IO.File.ReadAllBytesAsync(imagePath);

                if (resizeToWidth > 0)
                {
                    using var image = Image.Load(imageBytes);
                    if (image.Width > resizeToWidth)
                    {
                        image.Mutate(x => x.Resize(resizeToWidth, 0));
                        using var ms = new MemoryStream();
                        image.SaveAsJpeg(ms);
                        imageBytes = ms.ToArray();
                    }
                }

                
                var imageBase64 = Convert.ToBase64String(imageBytes);

                var requestBody = new
                {
                    model = aiModel,
                    prompt = prompt,
                    images = new[] { imageBase64 },
                    stream = false,
                    options = new
                    {
                        num_predict = numPredict,
                        temperature = temperature
                    }
                };

                var json = System.Text.Json.JsonSerializer.Serialize(requestBody);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

                var response = await httpClient.PostAsync($"{aiApiUrl}/api/generate", content);
                response.EnsureSuccessStatusCode();

                var responseJson = await response.Content.ReadAsStringAsync();
                var ollamaResponse = System.Text.Json.JsonSerializer.Deserialize<OllamaResponse>(responseJson);

                var description = ollamaResponse?.response?.Trim();
                if (string.IsNullOrEmpty(description))
                    return null;

                return $"AI: {description}";
            }
            catch (Exception ex)
            {
                Program.Log("ai", eventId, camera, $"API call failed: {ex.Message}");
                return null;
            }
        }


        private async Task UpdateTelegramMessageAsync(AITask task, string description)
        {
            try
            {
                var caption = task.OriginalCaption + "\n\n" + description;

                await tgBotClient.EditMessageCaptionAsync(
                    chatId: task.ChatId,
                    messageId: task.MessageId,
                    caption: caption
                );

                Program.Log("ai", task.EventId, task.Camera, $"Telegram message updated: {task.ChatId}/{task.MessageId}");
            }
            catch (Exception ex)
            {
                Program.Log("ai", task.EventId, task.Camera, $"Failed to update Telegram message: {ex.Message}");
            }
        }

        public int GetQueueSize()
        {
            return _queue.Count;
        }
    }
}
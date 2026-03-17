using System.Collections.Concurrent;
using Telegram.Bot;

namespace frte2tg
{
    public class FRTask
    {
        public List<string> ImagePaths { get; set; }
        public long ChatId { get; set; }
        public int MessageId { get; set; }
        public string Camera { get; set; }
        public string EventId { get; set; }
        public string OriginalCaption { get; set; }
        public string AIPrompt { get; set; }
        public DateTime QueuedAt { get; set; }
    }

    public class FRQueueService
    {
        private readonly ConcurrentQueue<FRTask> queue = new ConcurrentQueue<FRTask>();
        private readonly HttpClient httpClient;
        private readonly ITelegramBotClient tgBotClient;
        private readonly SemaphoreSlim semaphore;
        private readonly CancellationTokenSource cts;
        private readonly string frApiUrl;
        private readonly string frApiKey;
        private readonly double confidence;
        private readonly double detProbThreshold;

        private Task workerTask;

        public FRQueueService(ITelegramBotClient botClient, FRSettings frSettings)
        {
            frApiUrl = frSettings.url;
            frApiKey = frSettings.apikey;
            confidence = frSettings.confidence;
            detProbThreshold = frSettings.detprobthreshold;
            tgBotClient = botClient;
            httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            httpClient.DefaultRequestHeaders.Add("x-api-key", frApiKey);
            semaphore = new SemaphoreSlim(2);
            cts = new CancellationTokenSource();
        }

        public void Start()
        {
            workerTask = Task.Run(() => ProcessQueueAsync(cts.Token));
            Program.Log("app", "", "", "FR queue service started");
        }

        public void Stop()
        {
            cts.Cancel();
            workerTask?.Wait();
            Program.Log("fr", "", "", "Queue service stopped");
        }

        public void AddToQueue(FRTask task)
        {
            task.QueuedAt = DateTime.Now;
            queue.Enqueue(task);
            Program.Log("fr", task.EventId, task.Camera, $"Added to queue ({queue.Count} in queue)");
        }

        private async Task ProcessQueueAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (queue.TryDequeue(out var task))
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
                    Program.Log("fr", "", "", $"Queue processing error: {ex.Message}");
                    await Task.Delay(5000, cancellationToken);
                }
            }
        }

        private async Task ProcessTaskAsync(FRTask task)
        {
            try
            {
                Program.Log("fr", task.EventId, task.Camera, $"Processing {task.ImagePaths.Count} images, queued {(DateTime.Now - task.QueuedAt).TotalSeconds:F1}s ago");

                var allNames = new List<string>();

                foreach (var path in task.ImagePaths)
                {
                    if (!System.IO.File.Exists(path))
                    {
                        Program.Log("fr", task.EventId, task.Camera, $"Image not found: {path}");
                        continue;
                    }

                    var names = await CallFRApiAsync(path, task.EventId, task.Camera);
                    foreach (var name in names)
                        if (!allNames.Contains(name))
                            allNames.Add(name);
                }

                Program.Log("fr", task.EventId, task.Camera, allNames.Count > 0
                    ? $"Recognized: {string.Join(", ", allNames)}"
                    : "No faces recognized");

                if (Program.goAI)
                {
                    string enrichedPrompt = task.AIPrompt;
                    if (allNames.Count > 0)
                        enrichedPrompt = $"На фото: {string.Join(", ", allNames)}. " + task.AIPrompt;

                    Program.aiQueue.AddToQueue(new AITask
                    {
                        ImagePaths = task.ImagePaths,
                        Prompt = enrichedPrompt,
                        ChatId = task.ChatId,
                        MessageId = task.MessageId,
                        Camera = task.Camera,
                        EventId = task.EventId,
                        OriginalCaption = task.OriginalCaption
                    });
                }
                else if (allNames.Count > 0)
                {
                    await UpdateTelegramMessageAsync(task, "👤 " + string.Join(", ", allNames));
                }
            }
            catch (Exception ex)
            {
                Program.Log("fr", task.EventId, task.Camera, $"Task failed: {ex.Message}");
            }
        }

        private async Task<List<string>> CallFRApiAsync(string imagePath, string eventId, string camera)
        {
            var result = new List<string>();
            try
            {
                using var form = new MultipartFormDataContent();
                var imageBytes = await System.IO.File.ReadAllBytesAsync(imagePath);
                form.Add(new ByteArrayContent(imageBytes), "file", System.IO.Path.GetFileName(imagePath));

                var response = await httpClient.PostAsync(
                    $"{frApiUrl}/api/v1/recognition/faces/?limit=0&det_prob_threshold={detProbThreshold}",
                    form);

                if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                {
                    //Program.Log("fr", eventId, camera, "No face found in image");
                    return result;
                }

                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                var fr = System.Text.Json.JsonSerializer.Deserialize<CompreFaceResponse>(json);
                if (fr?.result == null) return result;

                foreach (var face in fr.result)
                {
                    if (face.subjects == null || face.subjects.Count == 0) continue;
                    var best = face.subjects.MaxBy(s => s.similarity);
                    if (best != null && best.similarity >= confidence && !string.IsNullOrEmpty(best.subject))
                        result.Add(best.subject);
                }
            }
            catch (Exception ex)
            {
                Program.Log("fr", eventId, camera, $"API call failed: {ex.Message}");
            }
            return result;
        }

        private async Task UpdateTelegramMessageAsync(FRTask task, string text)
        {
            try
            {
                await tgBotClient.EditMessageCaptionAsync(
                    chatId: task.ChatId,
                    messageId: task.MessageId,
                    caption: task.OriginalCaption + "\n\n" + text);
                Program.Log("fr", task.EventId, task.Camera, $"Telegram message updated: {task.ChatId}/{task.MessageId}");
            }
            catch (Exception ex)
            {
                Program.Log("fr", task.EventId, task.Camera, $"Failed to update Telegram message: {ex.Message}");
            }
        }

        public int GetQueueSize() => queue.Count;
    }

    public class CompreFaceResponse
    {
        public List<CompreFaceFace> result { get; set; }
    }

    public class CompreFaceFace
    {
        public List<CompreFaceSubject> subjects { get; set; }
    }

    public class CompreFaceSubject
    {
        public string subject { get; set; }
        public double similarity { get; set; }
    }
}
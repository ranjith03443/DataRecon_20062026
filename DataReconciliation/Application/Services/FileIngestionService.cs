using DataReconciliation.Application.Interfaces;
using DataReconciliation.Domain.Enums;
using DataReconciliation.Domain.Models;
using Microsoft.Extensions.Logging;

namespace DataReconciliation.Application.Services
{
    public class FileIngestionService : IFileIngestionService
    {
        private readonly ILogger<FileIngestionService> _logger;
        private readonly string _baseWorkflowPath;

        public FileIngestionService(ILogger<FileIngestionService> logger, IConfiguration configuration)
        {
            _logger = logger;
            _baseWorkflowPath = configuration["WorkflowStorage:BasePath"] ?? Path.Combine(Directory.GetCurrentDirectory(), "workflow");
        }

        public async Task<string> IngestFileAsync(string jobId, Stream fileStream, string fileName, DatasetRole role)
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Starting file ingestion. JobId={JobId} FileName={FileName} Role={Role}", jobId, fileName, role);

            var inputPath = Path.Combine(_baseWorkflowPath, jobId, "input");
            Directory.CreateDirectory(inputPath);

            var filePath = Path.Combine(inputPath, fileName);
            await using var outputStream = File.Create(filePath);

            // Stream to disk without loading entire file into memory
            const int bufferSize = 81920; // 80KB buffer
            var buffer = new byte[bufferSize];
            int bytesRead;
            long totalBytes = 0;

            while ((bytesRead = await fileStream.ReadAsync(buffer)) > 0)
            {
                await outputStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalBytes += bytesRead;
            }

            stopwatch.Stop();
            _logger.LogInformation("File ingested successfully. JobId={JobId} FileName={FileName} Size={SizeBytes} Duration={Duration}ms",
                jobId, fileName, totalBytes, stopwatch.ElapsedMilliseconds);

            return filePath;
        }

        public Task<bool> ValidateFileAsync(string filePath, DatasetType type)
        {
            _logger.LogInformation("Validating file. FilePath={FilePath} Type={Type}", filePath, type);

            if (!File.Exists(filePath))
            {
                _logger.LogWarning("File not found during validation. FilePath={FilePath}", filePath);
                return Task.FromResult(false);
            }

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            var valid = type switch
            {
                DatasetType.CSV => extension == ".csv",
                DatasetType.EXCEL => extension == ".xlsx" || extension == ".xls",
                DatasetType.JSON => extension == ".json",
                DatasetType.DAT => extension == ".dat",
                DatasetType.TXT => extension == ".txt",
                _ => false
            };

            _logger.LogInformation("File validation result. FilePath={FilePath} Valid={Valid}", filePath, valid);
            return Task.FromResult(valid);
        }

        public async Task<string> GetWorkflowPathAsync(string jobId, string subfolder)
        {
            var path = Path.Combine(_baseWorkflowPath, jobId, subfolder);
            Directory.CreateDirectory(path);
            return await Task.FromResult(path);
        }

        public void EnsureWorkflowDirectories(string jobId)
        {
            var subfolders = new[] { "input", "artifacts", "reports", "logs" };
            foreach (var sub in subfolders)
            {
                var path = Path.Combine(_baseWorkflowPath, jobId, sub);
                Directory.CreateDirectory(path);
                _logger.LogDebug("Ensured directory exists. Path={Path}", path);
            }
        }
    }
}

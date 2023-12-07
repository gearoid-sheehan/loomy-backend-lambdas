using Amazon.Batch;
using Amazon.Batch.Model;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.DataModel;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.S3;
using Amazon.S3.Model;
using DocumentFormat.OpenXml.InkML;
using DocumentFormat.OpenXml.Spreadsheet;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json;

// Assembly attribute to enable the Lambda function's JSON input to be converted into a .NET class.
[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace processLeadsSpreadsheet;

public class Function
{
    private readonly string _environmentName = "";
    public IConfiguration Configuration { get; private set; }
    private readonly IAmazonS3 _s3Client;
    private readonly IAmazonDynamoDB _dynamoDBClient;
    private readonly DynamoDBContext _dynamoDBContext;
    private readonly IAmazonBatch _batchClient;
    private readonly string _s3BucketLeadsSpreadsheets = "";

    public Function()
    {
        _environmentName = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "";

        Configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{_environmentName}.json", optional: true)
            .Build();

        _s3Client = new AmazonS3Client();
        _dynamoDBClient = new AmazonDynamoDBClient();
        _dynamoDBContext = new DynamoDBContext(_dynamoDBClient);
        _batchClient = new AmazonBatchClient();
        _s3BucketLeadsSpreadsheets = Configuration.GetValue<string>("AWS:S3BucketLeadsSpreadsheets") ?? "";
    }

    public async Task FunctionHandler(SQSEvent evnt, ILambdaContext context)
    {
        foreach (var message in evnt.Records)
        {
            await ProcessMessageAsync(message, context);
        }
    }

    public async Task ProcessMessageAsync(SQSEvent.SQSMessage message, ILambdaContext context)
    {
        var sqsDTO = JsonConvert.DeserializeObject<IncomingSQSDTO>(message.Body);

        context.Logger.LogLine($"Project Id: {sqsDTO?.ProjectId}");
        context.Logger.LogLine($"Downloading spreadsheet {sqsDTO?.ExcelFileURL} from bucket {_s3BucketLeadsSpreadsheets} with the id {sqsDTO?.ExcelFileS3Key}");

        var request = new GetObjectRequest
        {
            BucketName = _s3BucketLeadsSpreadsheets,
            Key = sqsDTO?.ExcelFileS3Key
        };

        using (var response = await _s3Client.GetObjectAsync(request))
        {
            using (var reader = new StreamReader(response.ResponseStream))
            {
                bool isFirstLine = true;

                int numberOfLeads = 0;

                while (!reader.EndOfStream)
                {
                    var line = await reader.ReadLineAsync();
                    var values = line?.Split(','); // Assuming comma-separated values

                    if (isFirstLine != true)
                    {
                        if (values?.Length >= 3) // Ensure the row has at least 3 values
                        {
                            string leadId = Guid.NewGuid().ToString();

                            string json = "{\"Email\": \"" + sqsDTO?.Email + "\", \"LeadId\": \"" + leadId + "\", " +
                                            "\"TabOne\": \"" + values[0] + "\", \"GreenscreenTemplateVideoS3Key\": \"" + sqsDTO?.GreenscreenTemplateVideoS3Key + "\"" +
                                            "\", \"LeadProcessedVideoTitle\": \"" + values[1] + "\"}";

                            Lead lead = new()
                            {
                                Id = leadId,
                                SortKey = leadId,
                                ProjectId = sqsDTO?.ProjectId ?? "",
                                TabOne = values[0],
                                VimeoLink = "Not yet uploaded",
                                LeadProcessedVideoTitle = values[1],
                                IsProcessed = false,
                                VideoProcessingJSON = json,
                                AWSBatchJobArrayIndex = numberOfLeads
                            };

                            await SaveLeadToDynamoDB(lead);
                        }

                        numberOfLeads += 1;
                    }
                    else
                    {
                        isFirstLine = false;
                    }
                }

                await UpdateProjectLeadNumberDynamoDB(sqsDTO?.ProjectId ?? "", numberOfLeads);

                RunAWSBatchJob(context, numberOfLeads, sqsDTO?.ProjectId ?? "");
            }
        }
    }

    private async Task<Lead> SaveLeadToDynamoDB(Lead lead)
    {
        await _dynamoDBContext.SaveAsync(lead);
        return lead;
    }

    private async Task UpdateProjectLeadNumberDynamoDB(string projectId, int numberOfLeads)
    {
        Dictionary<string, AttributeValueUpdate> updates = new()
        {
            ["TotalNumberOfLeads"] = new AttributeValueUpdate()
            {
                Action = AttributeAction.PUT,
                Value = new AttributeValue { N = numberOfLeads.ToString() }
            }
        };

        var request = new UpdateItemRequest
        {
            TableName = "Projects",
            Key = new Dictionary<string, AttributeValue>
                {
                    {
                        "Id", new AttributeValue { S = projectId }
                    }

                    ,
                    {
                        "SortKey", new AttributeValue { S =  projectId }
                    }
                },

            AttributeUpdates = updates
        };

        await _dynamoDBClient.UpdateItemAsync(request);
    }

    private void RunAWSBatchJob(ILambdaContext context, int numberOfLeads, string projectId)
    {
        var jobName = "MyBatchJob";
        var jobDefinition = "aws-batch-job-definition-test"; // Replace with your job definition name
        var jobQueue = "aws-batch-demo-queue"; // Replace with your job queue name
        var taskCount = numberOfLeads; // Set the number of tasks
        var shareIdentifier = "ShareIdentifier";

        // Define the parameters to pass to your job
        Dictionary<string, string> jobParameters = new Dictionary<string, string>
        {
            { "arg1", projectId }
        };

        var submitJobRequest = new SubmitJobRequest
        {
            JobName = jobName,
            JobDefinition = jobDefinition,
            JobQueue = jobQueue,
            ShareIdentifier = shareIdentifier,
            Parameters = jobParameters,
            ArrayProperties = new ArrayProperties
            {
                Size = taskCount
            }
        };

        var response = _batchClient.SubmitJobAsync(submitJobRequest);

        // Log or process the response as needed
        context.Logger.LogLine($"Job ID: {response.Result.JobId}");
    }

    //FOR EXCEL WHEN IMPLEMENTING LATER
    //using (var response = await _s3Client.GetObjectAsync(request))
    //{
    //    using (var memoryStream = new MemoryStream())
    //    {
    //        await response.ResponseStream.CopyToAsync(memoryStream);
    //        memoryStream.Seek(0, SeekOrigin.Begin);

    //        using (var spreadsheetDocument = SpreadsheetDocument.Open(memoryStream, false))
    //        {
    //            var workbookPart = spreadsheetDocument.WorkbookPart;
    //            var sheet = workbookPart.Workbook.Sheets.GetFirstChild<Sheet>();
    //            var worksheetPart = (WorksheetPart)workbookPart.GetPartById(sheet.Id);
    //            var worksheet = worksheetPart.Worksheet;

    //            var rows = worksheet.Descendants<Row>();

    //            foreach (var row in rows)
    //            {
    //                foreach (var cell in row.Descendants<Cell>())
    //                {
    //                    // Get the cell value using cell.InnerText or cell.CellValue.InnerText
    //                    var cellValue = cell.InnerText;
    //                    Console.WriteLine(cellValue);
    //                }
    //            }
    //        }

    //    }
    //}
}

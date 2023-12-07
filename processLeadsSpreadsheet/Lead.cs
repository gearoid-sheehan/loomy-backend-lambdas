using Amazon.DynamoDBv2.DataModel;

namespace processLeadsSpreadsheet
{
    [DynamoDBTable("Leads")]
    internal class Lead
    {
        public Lead()
        {

        }

        [DynamoDBHashKey]
        public string Id { get; set; } = "";

        [DynamoDBProperty]
        public string SortKey { get; set; } = "";

        [DynamoDBProperty]
        public string ProjectId { get; set; } = "";

        [DynamoDBProperty]
        public string TabOne { get; set; } = "";

        [DynamoDBProperty]
        public string VimeoLink { get; set; } = "";

        [DynamoDBProperty]
        public string LeadProcessedVideoTitle { get; set; } = "";

        [DynamoDBProperty]
        public bool IsProcessed { get; set; } = false;

        [DynamoDBProperty]
        public string VideoProcessingJSON { get; set; } = "";

        [DynamoDBProperty]
        public int AWSBatchJobArrayIndex { get; set; }
    }
}

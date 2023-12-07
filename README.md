# Loomy.Backend
All Lambdas should be written in .NET 6 (C#/PowerShell) where possible.<br><br>
**Lambda Naming Conventions:**<br>
- When creating a Lambda locally in the Loomy.Backend solution, camelCase naming convention should be adhered to.<br>
Example: *uploadProject*
- When creating a Lambda in AWS, camelCase naming convention should be adhered to with the development environment following the Lambdas name.<br>
Example: *uploadProjectDev, uploadProjectProd*<br><br>

**Lambda Error Handling/AWS Cloudwatch Alarms Naming Conventions:**<br><br>
When handling an error in an ASP .NET Core Lambda and creating a corresponding AWS Cloudwatch Alarm, PascalCase naming convention with the following set up should be adhered to.

**CloudWatch Metric Details:**
- Metric Filter Pattern - "{{Error}} |"<br>
Example: *"Exception |"*
- Metric Filter Name - "Lambda{{Error}}"<br>
Example: *"LambdaException"*
- Metric Namespace - "{{LambdaName}}Lambda"<br>
Example: *"uploadProjectLambda"*
- Metric Name - "Lambda{{Error}}"<br>
Example: *"LambdaException"*
- Metric Value - 1
- Default Value - 0
- Unit - Count<br><br>

**CloudWatch Alarm Details:**
- Metric Name - "Lambda{{Error}}"<br>
Example: *"LambdaException"*
- Statistic - Maximum
- Period - 5 min
- Threshold Type - Static
- Alarm Condition - Greater/Equal (>= threshold)
- Threshold Value - 1
- Data Points to Alarm - 1 out of 1
- Missing Data Treatment - Treat missing data as good
- Define Alarm State Trigger - In alarm
- Define the SNS - Select an existing SNS topic
- Alarm Name - "{{LambdaName}}Lambda{{Error}}{{Environment}}"<br>
Example: *"uploadProjectLambdaExceptionProd"*
- Alarm Description - "An alarm which is triggered when an exception occurs in the {{LambdaName}}{{Environment}} lambda."<br>
Example: *"An alarm which is triggered when an exception occurs in the uploadProjectProd lambda."*

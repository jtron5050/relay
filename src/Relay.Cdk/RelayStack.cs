using Amazon.CDK;
using Amazon.CDK.AWS.Cognito;
using Amazon.CDK.AWS.DynamoDB;
using Amazon.CDK.AWS.S3;
using Constructs;

namespace Relay.Cdk;

public class RelayStack : Stack
{
    public RelayStack(Construct scope, string id, IStackProps? props = null) : base(scope, id, props)
    {
        var table = new Table(this, "OperationalData", new TableProps
        {
            PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "PK", Type = AttributeType.STRING },
            SortKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = "SK", Type = AttributeType.STRING },
            BillingMode = BillingMode.PAY_PER_REQUEST,
            Encryption = TableEncryption.AWS_MANAGED,
            PointInTimeRecoverySpecification = new PointInTimeRecoverySpecification
            {
                PointInTimeRecoveryEnabled = true,
                RecoveryPeriodInDays = 35
            },
            DeletionProtection = true,
            RemovalPolicy = RemovalPolicy.RETAIN,
            Stream = StreamViewType.NEW_AND_OLD_IMAGES,
            // One numeric Unix-seconds cleanup attribute; writers own retention gates.
            TimeToLiveAttribute = "expiresAt"
        });
        foreach (var index in new[] { "GSI1", "GSI2" })
        {
            table.AddGlobalSecondaryIndex(new GlobalSecondaryIndexProps
            {
                IndexName = index,
                PartitionKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = $"{index}PK", Type = AttributeType.STRING },
                SortKey = new Amazon.CDK.AWS.DynamoDB.Attribute { Name = $"{index}SK", Type = AttributeType.STRING },
                // The adapter hydrates base rows; payloads never enter listings.
                ProjectionType = ProjectionType.KEYS_ONLY
            });
        }

        var artifacts = PrivateBucket("Artifacts");
        var auditArchive = PrivateBucket("AuditArchive");
        var staff = new UserPool(this, "Staff", new UserPoolProps
        {
            SelfSignUpEnabled = false,
            SignInAliases = new SignInAliases { Username = true },
            SignInCaseSensitive = false,
            Mfa = Mfa.REQUIRED,
            MfaSecondFactor = new MfaSecondFactor { Otp = true, Sms = false },
            AccountRecovery = AccountRecovery.NONE,
            PasswordPolicy = new PasswordPolicy
            {
                MinLength = 14,
                RequireLowercase = true,
                RequireUppercase = true,
                RequireDigits = true,
                RequireSymbols = true,
                TempPasswordValidity = Duration.Days(1)
            },
            DeletionProtection = true,
            RemovalPolicy = RemovalPolicy.RETAIN
        });
        foreach (var name in new[] { "Administrator", "ReleaseOperator", "SupportViewer" })
        {
            _ = new CfnUserPoolGroup(this, $"Staff{name}", new CfnUserPoolGroupProps
            {
                UserPoolId = staff.UserPoolId,
                GroupName = name
            });
        }

        // SRP for controlled staff tooling now; hosted OAuth awaits a real UI.
        var client = new CfnUserPoolClient(this, "StaffClient", new CfnUserPoolClientProps
        {
            UserPoolId = staff.UserPoolId,
            GenerateSecret = false,
            ExplicitAuthFlows = new[] { "ALLOW_USER_SRP_AUTH", "ALLOW_REFRESH_TOKEN_AUTH" },
            AllowedOAuthFlowsUserPoolClient = false,
            SupportedIdentityProviders = new[] { "COGNITO" },
            PreventUserExistenceErrors = "ENABLED",
            EnableTokenRevocation = true,
            AccessTokenValidity = 15,
            IdTokenValidity = 15,
            RefreshTokenValidity = 8,
            AuthSessionValidity = 3,
            TokenValidityUnits = new CfnUserPoolClient.TokenValidityUnitsProperty
            {
                AccessToken = "minutes", IdToken = "minutes", RefreshToken = "hours"
            },
            ReadAttributes = new[] { "email", "email_verified" },
            WriteAttributes = new[] { "email" }
        });

        _ = new CfnOutput(this, "OperationalTableName", new CfnOutputProps { Value = table.TableName });
        _ = new CfnOutput(this, "OperationalTableStreamArn", new CfnOutputProps { Value = table.TableStreamArn! });
        _ = new CfnOutput(this, "ArtifactBucketName", new CfnOutputProps { Value = artifacts.BucketName });
        _ = new CfnOutput(this, "AuditArchiveBucketName", new CfnOutputProps { Value = auditArchive.BucketName });
        _ = new CfnOutput(this, "StaffUserPoolId", new CfnOutputProps { Value = staff.UserPoolId });
        _ = new CfnOutput(this, "StaffIssuer", new CfnOutputProps { Value = staff.UserPoolProviderUrl });
        _ = new CfnOutput(this, "StaffClientId", new CfnOutputProps { Value = client.Ref });
    }

    private Bucket PrivateBucket(string id)
    {
        return new Bucket(this, id, new BucketProps
        {
            Encryption = BucketEncryption.S3_MANAGED,
            Versioned = true,
            BlockPublicAccess = BlockPublicAccess.BLOCK_ALL,
            ObjectOwnership = ObjectOwnership.BUCKET_OWNER_ENFORCED,
            EnforceSSL = true,
            RemovalPolicy = RemovalPolicy.RETAIN,
            AutoDeleteObjects = false,
            // No expiration: published versions and audit archives remain durable.
            LifecycleRules = new[] { new LifecycleRule { AbortIncompleteMultipartUploadAfter = Duration.Days(7) } }
        });
    }
}

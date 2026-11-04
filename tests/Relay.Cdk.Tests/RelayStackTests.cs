using System.Text.Json;
using Amazon.CDK;
using Amazon.CDK.Assertions;
using Xunit;

namespace Relay.Cdk.Tests;

public class RelayStackTests
{
    private static Template Synthesize()
    {
        var app = new App(new AppProps { Outdir = Path.Combine(Path.GetTempPath(), $"relay-cdk-{Guid.NewGuid():N}") });
        return Template.FromStack(new RelayStack(app, "TestRelay"));
    }

    // jsii accepts dictionaries/arrays, not anonymous C# objects or JsonElement.
    private static Dictionary<string, object> Expected(string json)
    {
        using var document = JsonDocument.Parse(json);
        return (Dictionary<string, object>)Convert(document.RootElement);
    }

    private static object Convert(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => value.EnumerateObject().ToDictionary(p => p.Name, p => Convert(p.Value)),
        JsonValueKind.Array => value.EnumerateArray().Select(Convert).ToArray(),
        JsonValueKind.String => value.GetString()!,
        JsonValueKind.Number => value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new ArgumentException("Unsupported assertion value.")
    };

    private static JsonElement Resource(object resource) => JsonSerializer.SerializeToElement(resource);

    [Fact]
    public void OperationalTableMatchesAdapterAndProtectsHistory()
    {
        var template = Synthesize();
        template.ResourceCountIs("AWS::DynamoDB::Table", 1);
        template.HasResourceProperties("AWS::DynamoDB::Table", Expected("""
        {
          "BillingMode": "PAY_PER_REQUEST", "DeletionProtectionEnabled": true,
          "SSESpecification": {"SSEEnabled": true},
          "PointInTimeRecoverySpecification": {"PointInTimeRecoveryEnabled": true, "RecoveryPeriodInDays": 35},
          "StreamSpecification": {"StreamViewType": "NEW_AND_OLD_IMAGES"},
          "TimeToLiveSpecification": {"AttributeName": "expiresAt", "Enabled": true},
          "KeySchema": [{"AttributeName": "PK", "KeyType": "HASH"}, {"AttributeName": "SK", "KeyType": "RANGE"}],
          "GlobalSecondaryIndexes": [
            {"IndexName": "GSI1", "KeySchema": [{"AttributeName": "GSI1PK", "KeyType": "HASH"}, {"AttributeName": "GSI1SK", "KeyType": "RANGE"}], "Projection": {"ProjectionType": "KEYS_ONLY"}},
            {"IndexName": "GSI2", "KeySchema": [{"AttributeName": "GSI2PK", "KeyType": "HASH"}, {"AttributeName": "GSI2SK", "KeyType": "RANGE"}], "Projection": {"ProjectionType": "KEYS_ONLY"}}
          ]
        }
        """));
        var table = Resource(template.FindResources("AWS::DynamoDB::Table").Values.Single());
        AssertRetained(table);
        var attributes = table.GetProperty("Properties").GetProperty("AttributeDefinitions").EnumerateArray().ToArray();
        Assert.Equal(new[] { "GSI1PK", "GSI1SK", "GSI2PK", "GSI2SK", "PK", "SK" },
            attributes.Select(a => a.GetProperty("AttributeName").GetString()).Order().ToArray());
        Assert.All(attributes, a => Assert.Equal("S", a.GetProperty("AttributeType").GetString()));
    }

    [Fact]
    public void BothBucketsArePrivateEncryptedVersionedTlsOnlyAndRetained()
    {
        var template = Synthesize();
        template.ResourceCountIs("AWS::S3::Bucket", 2);
        template.ResourceCountIs("AWS::S3::BucketPolicy", 2);
        foreach (var (bucketId, bucket) in template.FindResources("AWS::S3::Bucket"))
        {
            var resource = Resource(bucket);
            AssertRetained(resource);
            var props = resource.GetProperty("Properties");
            Assert.Equal("Enabled", props.GetProperty("VersioningConfiguration").GetProperty("Status").GetString());
            Assert.All(props.GetProperty("PublicAccessBlockConfiguration").EnumerateObject(), flag => Assert.True(flag.Value.GetBoolean()));
            Assert.Equal(4, props.GetProperty("PublicAccessBlockConfiguration").EnumerateObject().Count());
            Assert.Equal("AES256", props.GetProperty("BucketEncryption").GetProperty("ServerSideEncryptionConfiguration")[0]
                .GetProperty("ServerSideEncryptionByDefault").GetProperty("SSEAlgorithm").GetString());
            Assert.Equal("BucketOwnerEnforced", props.GetProperty("OwnershipControls").GetProperty("Rules")[0].GetProperty("ObjectOwnership").GetString());
            var lifecycle = props.GetProperty("LifecycleConfiguration").GetProperty("Rules");
            var rule = Assert.Single(lifecycle.EnumerateArray());
            Assert.Equal(new[] { "AbortIncompleteMultipartUpload", "Status" }, rule.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Equal(7, rule.GetProperty("AbortIncompleteMultipartUpload").GetProperty("DaysAfterInitiation").GetInt32());
            var policy = Assert.Single(template.FindResources("AWS::S3::BucketPolicy").Values.Select(Resource),
                p => p.GetProperty("Properties").GetProperty("Bucket").GetProperty("Ref").GetString() == bucketId);
            var statement = Assert.Single(policy.GetProperty("Properties").GetProperty("PolicyDocument").GetProperty("Statement").EnumerateArray());
            Assert.Equal("Deny", statement.GetProperty("Effect").GetString());
            Assert.Equal("s3:*", statement.GetProperty("Action").GetString());
            Assert.Equal("*", statement.GetProperty("Principal").GetProperty("AWS").GetString());
            Assert.Equal("false", statement.GetProperty("Condition").GetProperty("Bool").GetProperty("aws:SecureTransport").GetString());
            var resources = statement.GetProperty("Resource");
            Assert.Equal(bucketId, resources[0].GetProperty("Fn::GetAtt")[0].GetString());
            Assert.Equal(bucketId, resources[1].GetProperty("Fn::Join")[1][0].GetProperty("Fn::GetAtt")[0].GetString());
            Assert.Equal("/*", resources[1].GetProperty("Fn::Join")[1][1].GetString());
        }
    }

    [Fact]
    public void StaffAreAdministrativelyProvisionedWithMandatoryTotpAndLimitedClient()
    {
        var template = Synthesize();
        template.ResourceCountIs("AWS::Cognito::UserPool", 1);
        template.HasResourceProperties("AWS::Cognito::UserPool", Expected("""
        {
          "AdminCreateUserConfig": {"AllowAdminCreateUserOnly": true},
          "MfaConfiguration": "ON", "EnabledMfas": ["SOFTWARE_TOKEN_MFA"],
          "DeletionProtection": "ACTIVE", "UsernameConfiguration": {"CaseSensitive": false},
          "AccountRecoverySetting": {"RecoveryMechanisms": [{"Name": "admin_only", "Priority": 1}]},
          "Policies": {"PasswordPolicy": {"MinimumLength": 14, "RequireLowercase": true, "RequireUppercase": true, "RequireNumbers": true, "RequireSymbols": true, "TemporaryPasswordValidityDays": 1}}
        }
        """));
        var pool = template.FindResources("AWS::Cognito::UserPool").Single();
        AssertRetained(Resource(pool.Value));
        var groups = template.FindResources("AWS::Cognito::UserPoolGroup").Values.Select(Resource).ToArray();
        Assert.Equal(new[] { "Administrator", "ReleaseOperator", "SupportViewer" }, groups.Select(g => g.GetProperty("Properties").GetProperty("GroupName").GetString()).Order().ToArray());
        Assert.All(groups, group =>
        {
            Assert.Equal(pool.Key, group.GetProperty("Properties").GetProperty("UserPoolId").GetProperty("Ref").GetString());
            Assert.False(group.GetProperty("Properties").TryGetProperty("RoleArn", out _));
        });
        template.ResourceCountIs("AWS::Cognito::UserPoolClient", 1);
        template.HasResourceProperties("AWS::Cognito::UserPoolClient", Expected("""
        {
          "GenerateSecret": false, "ExplicitAuthFlows": ["ALLOW_USER_SRP_AUTH", "ALLOW_REFRESH_TOKEN_AUTH"],
          "AllowedOAuthFlowsUserPoolClient": false, "EnableTokenRevocation": true, "PreventUserExistenceErrors": "ENABLED",
          "AccessTokenValidity": 15, "IdTokenValidity": 15, "RefreshTokenValidity": 8, "AuthSessionValidity": 3,
          "TokenValidityUnits": {"AccessToken": "minutes", "IdToken": "minutes", "RefreshToken": "hours"},
          "SupportedIdentityProviders": ["COGNITO"], "ReadAttributes": ["email", "email_verified"], "WriteAttributes": ["email"]
        }
        """));
        var client = Resource(template.FindResources("AWS::Cognito::UserPoolClient").Values.Single()).GetProperty("Properties");
        Assert.Equal(pool.Key, client.GetProperty("UserPoolId").GetProperty("Ref").GetString());
        Assert.False(client.TryGetProperty("CallbackURLs", out _));
        Assert.False(client.TryGetProperty("AllowedOAuthFlows", out _));
    }

    [Fact]
    public void OutputsExposeOnlyConfigurationAndNoWorkloadPermissionsExistYet()
    {
        var template = Synthesize();
        Assert.Equal(new[] { "ArtifactBucketName", "AuditArchiveBucketName", "OperationalTableName", "OperationalTableStreamArn", "StaffClientId", "StaffIssuer", "StaffUserPoolId" },
            template.FindOutputs("*").Keys.Order().ToArray());
        foreach (var type in new[] { "AWS::IAM::Role", "AWS::IAM::Policy", "AWS::Lambda::Function", "AWS::Cognito::UserPoolUser" })
            template.ResourceCountIs(type, 0);
    }

    private static void AssertRetained(JsonElement resource)
    {
        Assert.Equal("Retain", resource.GetProperty("DeletionPolicy").GetString());
        Assert.Equal("Retain", resource.GetProperty("UpdateReplacePolicy").GetString());
    }
}

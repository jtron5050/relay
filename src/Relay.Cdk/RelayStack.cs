using Amazon.CDK;
using Constructs;

namespace Relay.Cdk;

public class RelayStack : Stack
{
    internal RelayStack(Construct scope, string id, IStackProps? props = null) : base(scope, id, props)
    {
        // The code that defines your stack goes here
    }
}
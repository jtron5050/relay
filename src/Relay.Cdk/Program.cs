using Amazon.CDK;
using Relay.Cdk;

var app = new App();

new RelayStack(app, "RelayStack", new StackProps
{
    
});

app.Synth();


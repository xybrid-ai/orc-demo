using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UnityEngine;

class Program {
 static object Invoke(string method,params object[] args)=>typeof(GatekeeperNpcModel).GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);
 static void Main(string[] args) {
  var options=new JsonSerializerOptions{IncludeFields=true};
  var doc=JsonSerializer.Deserialize<GatekeeperStory.Document>(File.ReadAllText(args[0]),options);
  var scenario=new GatekeeperScenario{initialPatience=3,maxTurns=5,captureName=true,lines=doc.lines,authoredConversation=true,authoredSource=new TextAsset{text=File.ReadAllText(args[0])}};
  // Read the real serialized mood profiles; this is a fixture-specific YAML reader.
  var asset=File.ReadAllText(Path.Combine(Path.GetDirectoryName(args[0]),"TrolaGatekeeper.asset"));
  var section=asset.IndexOf("  voices:",StringComparison.Ordinal);
  if(section<0)throw new InvalidOperationException("Missing serialized voice profiles");
  var profiles=Regex.Matches(asset.Substring(section),@"(?m)^  - mood: (\d+)\r?\n    pitch: ([0-9.]+)\r?\n    volume: ([0-9.]+)\r?\n    anger: ([0-9.]+)\r?\n    amusement: ([0-9.]+)\r?$");
  foreach(Match profile in profiles) {
   float Value(int index)=>float.Parse(profile.Groups[index].Value,CultureInfo.InvariantCulture);
   scenario.voices.Add(new GatekeeperScenario.VoiceProfile{mood=(GatekeeperMood)int.Parse(profile.Groups[1].Value,CultureInfo.InvariantCulture),pitch=Value(2),volume=Value(3),anger=Value(4),amusement=Value(5)});
  }
  var tags=new HashSet<string>(doc.lines.Select(l=>l.emotion));
  var card=(string)Invoke("CharacterCard",scenario,tags);var grammar=(string)Invoke("LineGrammar",tags);
  string input;
  while((input=Console.ReadLine())!=null) {
   var req=JsonNode.Parse(input);var command=req["command"].GetValue<string>();
   if(command=="decision_checks") {
    Console.WriteLine(JsonSerializer.Serialize(DecisionChecks.Run(scenario)));continue;
   }
   if(command=="client_checks") {
    Console.WriteLine(JsonSerializer.Serialize(DecisionChecks.Client(scenario,req["url"].GetValue<string>())));continue;
   }
   if(command=="story_checks") {
    if(!scenario.Validate(out var validationError))throw new InvalidOperationException(validationError);
    var results=new List<object>();
    foreach(var plan in new[]{new[]{0,1,0},new[]{0,3,1},new[]{2,2,2}}) {
     var story=new GatekeeperStory(scenario);story.CaptureName("Mira",GatekeeperNames.DefaultBlocklist,"Worm");story.Advance();story.CaptureBusiness("selling turnips",GatekeeperNames.DefaultBlocklist);story.Advance();
     foreach(var choice in plan) {if(!story.Choose(choice))throw new InvalidOperationException("Invalid scripted story choice");while(story.Advance()){} }
     story.Finish();results.Add(new{choices=plan,patience=story.Encounter.Patience,outcome=story.Encounter.Outcome.ToString()});
    }
    Console.WriteLine(JsonSerializer.Serialize(results));continue;
   }
   if(command=="turn_checks") {
    // Drives the real AuthoredGatekeeperLineSource as the controller does: one input, then Continue until a checkpoint,
    // applying each line's patience_delta when it plays. Steps: T<text> typed, C<i> choice, X<i> choice interrupted
    // after its first clip, I idle taunt left unanswered (Continue after it), K<i> idle taunt answered by choice i.
    // Each route runs with sample slots (Mira / selling turnips) and again with the longest names the game allows.
    // The long inputs end like typed sentences: the trailing punctuation must be dropped, not turn them into "no name"/"no business".
    string longName="Bartholomew Rook.", longBusiness="delivering a cartload of pickled turnips to my old aunt Ysol!";
    var routes=new[]{"C0 C1 C0","C0 C3 C1","C2 C2 C2","C3 C3 C1","C1 C1 C1","C3 C0 C2","C0 C2 C0","C2 C0 C1","X0 C1 C0","K3 C1 C0","I C3 C3 C1"};
    var turns=new List<object>();
    foreach(var longSlots in new[]{false,true})
    foreach(var route in routes) {
     var conv=new GatekeeperConversationState(new GatekeeperEncounter(3,5,true));
     var source=new AuthoredGatekeeperLineSource(scenario,conv);
     var steps=new List<string>{"S","T"+(longSlots?longName:"Mira"),"T"+(longSlots?longBusiness:"selling turnips")};
     steps.AddRange(route.Split(' '));
     foreach(var step in steps) {
      var lines=new List<object>();
      GatekeeperPlayerInput first=step[0]=='S'?new GatekeeperPlayerInput(GatekeeperInputKind.Start)
       :step[0]=='T'?new GatekeeperPlayerInput(GatekeeperInputKind.Text,step.Substring(1))
       :step[0]=='I'||step[0]=='K'?new GatekeeperPlayerInput(GatekeeperInputKind.Idle)
       :new GatekeeperPlayerInput(GatekeeperInputKind.Choice,choice:int.Parse(step.Substring(1)));
      GatekeeperScenario.Line Ask(GatekeeperPlayerInput input) {
       var l=source.NextLineAsync(conv,input,System.Threading.CancellationToken.None).GetAwaiter().GetResult();
       if(l!=null) {GatekeeperLineSources.ValidateOutput(l);conv.Encounter.ChangePatience(l.patience_delta);
        lines.Add(new{l.id,l.text,l.emotion,l.delivery,chars=l.text.Length,words=l.text.Split(' ',StringSplitOptions.RemoveEmptyEntries).Length});}
       return l;
      }
      var line=Ask(first);
      if(step[0]=='K') { // the click lands during the taunt: no Continue, the choice is the next input
       turns.Add(new{route,longSlots,step="taunt",patience=conv.Encounter.Patience,outcome=conv.Encounter.Outcome.ToString(),handback=true,choices=new string[0],lines});
       lines=new List<object>();line=Ask(new GatekeeperPlayerInput(GatekeeperInputKind.Choice,choice:int.Parse(step.Substring(1))));
      }
      if(step[0]=='X'&&line!=null) line=Ask(new GatekeeperPlayerInput(GatekeeperInputKind.Interrupt));
      while(line!=null) line=Ask(new GatekeeperPlayerInput(GatekeeperInputKind.Continue));
      var p=source.Presentation;
      bool handback=p.Choices.Count>0||p.InputSlot!=GatekeeperInputSlot.None||p.Ended||p.AwaitGate;
      turns.Add(new{route,longSlots,step,patience=conv.Encounter.Patience,outcome=conv.Encounter.Outcome.ToString(),handback,
       choices=p.Choices.Select(c=>c.label+" | "+c.say).ToArray(),lines});
     }
    }
    Console.WriteLine(JsonSerializer.Serialize(turns));continue;
   }
   if(command=="spirit") {
    var suite=Type.GetType(req["suite"].GetValue<string>())??throw new InvalidOperationException("Unknown spirit suite");
    var result=suite.GetMethod("Run",BindingFlags.Public|BindingFlags.Static).Invoke(null,new object[]{req,scenario,doc});
    Console.WriteLine(JsonSerializer.Serialize(result,result.GetType(),options));continue;
   }
   if(command=="parse") {
    var parsed=GatekeeperNpcModel.Parse(req["raw"].GetValue<string>(),tags);
    Console.WriteLine(JsonSerializer.Serialize(new {valid=parsed!=null,line=parsed},options));continue;
   }
   var encounter=new GatekeeperEncounter(req["patience"]?.GetValue<int>()??3,5,true);
   encounter.CaptureName(req["name"]?.GetValue<string>()??"Mira",GatekeeperNames.DefaultBlocklist,"Worm");
   encounter.CaptureBusiness(req["business"]?.GetValue<string>()??"selling turnips",GatekeeperNames.DefaultBlocklist);
   encounter.FinishStory(Enum.Parse<GatekeeperOutcome>(req["outcome"]?.GetValue<string>()??"Passed"));
   var state=new GatekeeperConversationState(encounter);
   foreach(var approach in req["approaches"]?.AsArray()??new JsonArray("bribe"))state.RecordApproach(approach.GetValue<string>());
   foreach(var h in req["history"]?.AsArray()??new JsonArray())state.Record(h["speaker"].GetValue<string>(),h["text"].GetValue<string>());
   if(req["offer"]!=null)state.RecordOffer(req["offer"].GetValue<string>());
   if(command=="plan") {
    var known=GatekeeperFactQuestions.Interpret(req["message"].GetValue<string>());
    var decision=new GatekeeperDecision(Enum.Parse<GatekeeperScope>(req["scope"].GetValue<string>()),
     Enum.Parse<GatekeeperDialogueIntent>(req["intent"].GetValue<string>()),known?.Facts??GatekeeperFact.None);
    var plan=GatekeeperResponsePolicy.Create(state.Snapshot(),decision);
    Console.WriteLine(JsonSerializer.Serialize(new{plan,line=plan.Expel?null:plan.Assemble("I built this wall. Mind your manners.")},options));continue;
   }
   string prompt=(string)Invoke("TurnPrompt",state.Snapshot(),req["message"].GetValue<string>());
   if(command=="apply") {
    var reply=GatekeeperNpcModel.Parse(req["raw"].GetValue<string>(),tags);
    if(reply!=null) {encounter.CountStoryTurn();encounter.ChangePatience(reply.patience_delta);}
    Console.WriteLine(JsonSerializer.Serialize(new {valid=reply!=null,patience=encounter.Patience,expulsion=encounter.Patience<=0,outcome=encounter.Outcome.ToString(),ended=reply!=null&&reply.ends_scene&&encounter.Patience>0}));continue;
   }
   Console.WriteLine(JsonSerializer.Serialize(new {persona=card,grammar,prompt,tags=tags.OrderBy(t=>t).ToArray()}));
  }
 }
}

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

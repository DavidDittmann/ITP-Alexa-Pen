using System;
using System.Configuration;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Lego.Ev3.Core;
using Lego.Ev3.Desktop;
using Amazon;
using Amazon.Runtime;
using Amazon.SQS;
using Amazon.SQS.Model;

namespace AlexaToEv3
{
    public class MainClass
    {
        #region CONSTANTS
        
        private const int TIMEOUT_IN_MS = 125;
        private const string ACCESS_KEY_ENV_NAME = "AWS_ACCESS_KEY";
        private const string SECRET_KEY_ENV_NAME = "AWS_SECRET_KEY";
        private const string EV3_PORT_KEY = "Ev3Port";
        private const string AWS_SQS_ADDRESS_KEY = "AwsSqsAddress";
        
        #endregion
        
        #region PRIVATE VARIABLES
        
        private static string _ev3Port;
        private static string _awsSqsAddress;
        private static AmazonSQSClient _sqsClient;
        private static Brick _brick;

        #endregion
        
        public static void Main(string[] args)
        {
            Configure();
            Task t = Execute();
            t.Wait();
            System.Console.ReadKey();
        }

        static async Task Execute()
        {
            _brick = new Brick(new BluetoothCommunication(_ev3Port));
            _brick.BrickChanged += _brick_BrickChanged;
            
            System.Console.WriteLine("Connecting...");
            await _brick.ConnectAsync();

            System.Console.WriteLine("Connected... Waiting for Commands...");
            await _brick.DirectCommand.PlayToneAsync(0x50, 5000, 500);

            while (true)
            {
                Ev3Command command = PollForQueueMessage();
                if (command != null)
                {
                    ProcessCommand(command);
                }
            }
        }

        private static void ProcessCommand(Ev3Command command)
        {
            switch (command.Action)
            {
                case "forward":
                    MoveForward(command);
                    break;
                case "backward":
                    MoveBackward(command);
                    break;
                case "left":
                    MoveLeft(command);
                    break;
                case "right":
                    MoveRight(command);
                    break;
                // Add more commands here as needed.
                default:
                    break;
            }

            System.Console.WriteLine("Command executed.");
        }

        private static async void MoveForward(Ev3Command command)
        {
            Console.WriteLine("\n\nMoving Forward...\n\n");

            uint distance = 30;
            if (!string.IsNullOrWhiteSpace(command.Value))
            {
                uint.TryParse(command.Value, out distance);
                Console.WriteLine("\nValue: {0}...\n\n", distance);
            }

            distance *= 100;
            _brick.BatchCommand.Initialize(CommandType.DirectNoReply);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.B, Polarity.Forward);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.C, Polarity.Forward);
            _brick.BatchCommand.StepMotorAtSpeed(OutputPort.B, 100, distance, false);
            _brick.BatchCommand.StepMotorAtSpeed(OutputPort.C, 100, distance, false);
            await _brick.BatchCommand.SendCommandAsync();
        }

        private static async void MoveBackward(Ev3Command command)
        {
            Console.WriteLine("Moving Backward...");

            uint distance = 30;
            if (!string.IsNullOrWhiteSpace(command.Value))
            {
                uint.TryParse(command.Value, out distance);
            }

            distance *= 100;
            _brick.BatchCommand.Initialize(CommandType.DirectNoReply);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.B, Polarity.Backward);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.C, Polarity.Backward);
            _brick.BatchCommand.StepMotorAtSpeed(OutputPort.B, 100, distance, false);
            _brick.BatchCommand.StepMotorAtSpeed(OutputPort.C, 100, distance, false);
            await _brick.BatchCommand.SendCommandAsync();
        }

        private static async void MoveLeft(Ev3Command command)
        {
            Console.WriteLine("Moving Left...");

            _brick.BatchCommand.Initialize(CommandType.DirectNoReply);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.B, Polarity.Backward);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.C, Polarity.Forward);
            _brick.BatchCommand.StepMotorAtPower(OutputPort.B, 100, 180, false);
            _brick.BatchCommand.StepMotorAtPower(OutputPort.C, 100, 180, false);
            await _brick.BatchCommand.SendCommandAsync();
        }
        private static async void MoveRight(Ev3Command command)
        {
            Console.WriteLine("Moving Right...");

            _brick.BatchCommand.Initialize(CommandType.DirectNoReply);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.B, Polarity.Forward);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.C, Polarity.Backward);
            _brick.BatchCommand.StepMotorAtPower(OutputPort.B, 100, 180, false);
            _brick.BatchCommand.StepMotorAtPower(OutputPort.C, 100, 180, false);
            await _brick.BatchCommand.SendCommandAsync();
        }

        static void _brick_BrickChanged(object sender, BrickChangedEventArgs e)
        {
            System.Console.WriteLine(e.Ports[InputPort.One].SIValue);
        }

        private static Ev3Command PollForQueueMessage()
        {

            ReceiveMessageRequest request = new ReceiveMessageRequest();
            request.QueueUrl = _awsSqsAddress;

            while (true)
            {
                DateTime timeout = DateTime.Now.AddMilliseconds(TIMEOUT_IN_MS);
                var responseTask = _sqsClient.ReceiveMessageAsync(request);

                // TODO: Replace with proper async completion handling.
                while (!responseTask.IsCompleted && DateTime.Now < timeout) { }

                ReceiveMessageResponse response = responseTask.Result;

                if (response.Messages != null && response.Messages.Count > 0)
                {
                    Message nextMessage = response.Messages.First();
                    DeleteMessageRequest deleteRequest = new DeleteMessageRequest();
                    deleteRequest.QueueUrl = _awsSqsAddress;
                    deleteRequest.ReceiptHandle = nextMessage.ReceiptHandle;

                    timeout = DateTime.Now.AddMilliseconds(TIMEOUT_IN_MS);
                    var deleteTask = _sqsClient.DeleteMessageAsync(deleteRequest);
                    while (!deleteTask.IsCompleted && DateTime.Now < timeout) { }

                    Console.WriteLine("Message: ");
                    Console.WriteLine("== " + nextMessage.Body);

                    var command = GetEv3CommandFromJson(nextMessage.Body);
                    return command;
                }
            }

        }

        private static Ev3Command GetEv3CommandFromJson(string text)
        {
            dynamic message = JsonConvert.DeserializeObject(text);
            string tmp = message.Message;
            //string action = message.action;
            //string val = message.value;

            dynamic lol = JsonConvert.DeserializeObject(tmp);
            string action = lol.action;
            string val = lol.value;

            Console.WriteLine("Found action/value pair of A: {0} V: {1}.", action, val);
            return new Ev3Command { Action = action, Value = val };
        }

        private static void Configure()
        {
            var appSettings = ConfigurationManager.AppSettings;
            _ev3Port = "COM3";
            _awsSqsAddress = "https://sqs.eu-west-1.amazonaws.com/697251208257/Test2Queue";
            
            string accessKey = Environment.GetEnvironmentVariable(ACCESS_KEY_ENV_NAME);
            string secretKey = Environment.GetEnvironmentVariable(SECRET_KEY_ENV_NAME);

            RegionEndpoint endpoint = RegionEndpoint.EUWest1;
            AWSCredentials credentials = new BasicAWSCredentials(accessKey, secretKey);

            _sqsClient = new AmazonSQSClient(credentials, endpoint);
        }
    }
}

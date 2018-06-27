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

        //Message polling timeout
        private const int TIMEOUT_IN_MS = 125;
        //Connection keys
        private const string ACCESS_KEY_ENV_NAME = "AWS_ACCESS_KEY";
        private const string SECRET_KEY_ENV_NAME = "AWS_SECRET_KEY";
        //Config file key names
        private const string EV3_PORT_KEY = "Ev3Port";
        private const string AWS_SQS_ADDRESS_KEY = "AwsSqsAddress";
        private const string STEPS_PER_DEGREE_KEY = "StepsPerDegreeTurn";

        #endregion

        #region PRIVATE VARIABLES

        private static string _ev3Port;
        private static string _awsSqsAddress;
        private static double _StepsPerDegreeTurn;
        private static AmazonSQSClient _sqsClient;
        private static Brick _brick;

        #endregion

        /* Main */
        public static void Main(string[] args)
        {
            //Load settings and connect to SQS queue
            Configure();
            //Run
            Task t = Execute();
            //Wait until done
            t.Wait();
            //Exit on input
            System.Console.ReadKey();
        }

        /* Loads settings and connects to SQS queue */
        private static void Configure()
        {
            //Load settings from app.config file
            var appSettings = ConfigurationManager.AppSettings;
            _ev3Port = appSettings[EV3_PORT_KEY] ?? "COM3";
            _awsSqsAddress = appSettings[AWS_SQS_ADDRESS_KEY] ?? string.Empty;
            _StepsPerDegreeTurn = Convert.ToDouble(appSettings[STEPS_PER_DEGREE_KEY] ?? "3.5");

            //Set AWS keys from constants
            string accessKey = Environment.GetEnvironmentVariable(ACCESS_KEY_ENV_NAME);
            string secretKey = Environment.GetEnvironmentVariable(SECRET_KEY_ENV_NAME);

            //Set AWS endpoint
            RegionEndpoint endpoint = RegionEndpoint.EUWest1;   //Set to 

            //Set AWS credentials
            AWSCredentials credentials = new BasicAWSCredentials(accessKey, secretKey);

            //Connect to SQS
            _sqsClient = new AmazonSQSClient(credentials, endpoint);
        }

        /* Main process: Connects to EV3 and loops */
        static async Task Execute()
        {
            //Connect to EV3
            _brick = new Brick(new BluetoothCommunication(_ev3Port));
            _brick.BrickChanged += _brick_BrickChanged;

            System.Console.WriteLine("Connecting...");
            await _brick.ConnectAsync();

            //Connection successful: show message and play sound
            System.Console.WriteLine("Connected... Waiting for Commands...");
            await _brick.DirectCommand.PlayToneAsync(0x50, 5000, 500);

            //Main loop, runs until terminated
            while (true)
            {
                //Get new message
                Ev3Command command = PollForQueueMessage();
                if (command != null)
                {
                    //Process message and control robot
                    ProcessCommand(command);
                }
            }
        }

        /* Prints port number */
        static void _brick_BrickChanged(object sender, BrickChangedEventArgs e)
        {
            System.Console.WriteLine(e.Ports[InputPort.One].SIValue);
        }

        /* Gets next message in queue */
        private static Ev3Command PollForQueueMessage()
        {
            //Prepare new request
            ReceiveMessageRequest request = new ReceiveMessageRequest();
            request.QueueUrl = _awsSqsAddress;
            //Loop
            while (true)
            {

                //Start timer
                DateTime timeout = DateTime.Now.AddMilliseconds(TIMEOUT_IN_MS);
                //Send request
                var responseTask = _sqsClient.ReceiveMessageAsync(request);
                // Wait until timeout
                while (!responseTask.IsCompleted && DateTime.Now < timeout) { }

                //Set response
                ReceiveMessageResponse response = responseTask.Result;

                //If messages were sent as response
                if (response.Messages != null && response.Messages.Count > 0)
                {
                    //Get first message in queue
                    Message nextMessage = response.Messages.First();
                    //Prepare message deletion request
                    DeleteMessageRequest deleteRequest = new DeleteMessageRequest();
                    deleteRequest.QueueUrl = _awsSqsAddress;
                    deleteRequest.ReceiptHandle = nextMessage.ReceiptHandle;

                    //Start timer
                    timeout = DateTime.Now.AddMilliseconds(TIMEOUT_IN_MS);
                    //Request message deletion
                    var deleteTask = _sqsClient.DeleteMessageAsync(deleteRequest);
                    //Wait until timeout
                    while (!deleteTask.IsCompleted && DateTime.Now < timeout) { }

                    //Show message (for debugging)
                    Console.WriteLine("Message: ");
                    Console.WriteLine("== " + nextMessage.Body);

                    //Get ev3 command from message
                    var command = GetEv3CommandFromJson(nextMessage.Body);
                    return command;
                }
            }

        }

        /* Convert SQS message body to EV3 command */
        private static Ev3Command GetEv3CommandFromJson(string text)
        {
            //Deserialize message
            dynamic message = JsonConvert.DeserializeObject(text);

            //Deserialize message content
            dynamic content = JsonConvert.DeserializeObject(message.Message);
            string deviceName = content.device;
            string action = content.action;
            string option = content.option;
            string value = content.value;

            //Show arguments received
            Console.WriteLine("Message received. Content:");
            if (deviceName != null) Console.WriteLine("Device \"{0}\"", deviceName);
            if (action != null) Console.WriteLine("Action \"{0}\"", action);
            if (option != null) Console.WriteLine("Option \"{0}\"", option);
            if (value != null) Console.WriteLine("Value \"{0}\"", value);
            //Create and return command from arguments
            return new Ev3Command { DeviceName = "", Action = action, Option = "", Value = value };
        }

        /* Interprets and executes commands */
        private static void ProcessCommand(Ev3Command command)
        {
            //Call function linked to action
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
                // More actions may be added here
                default:
                    break;
            }
            //Command successfully executed: show message
            System.Console.WriteLine("Command executed.");
        }

        /* Move robot forward */
        private static async void MoveForward(Ev3Command command)
        {
            //Show message
            Console.WriteLine("\n\nMoving Forward...\n\n");

            //Set distance in cm
            uint distance = 30; //Default to 30 cm
            if (!string.IsNullOrWhiteSpace(command.Value))
            {
                //If custom distance was given, set and display message
                uint.TryParse(command.Value, out distance);
                Console.WriteLine("\nValue: {0}...\n\n", distance);
            }

            //Multiply distance to get steps required
            distance *= 100;
            //Command robot
            _brick.BatchCommand.Initialize(CommandType.DirectNoReply);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.B, Polarity.Forward);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.C, Polarity.Forward);
            _brick.BatchCommand.StepMotorAtSpeed(OutputPort.B, 100, distance, false);
            _brick.BatchCommand.StepMotorAtSpeed(OutputPort.C, 100, distance, false);
            await _brick.BatchCommand.SendCommandAsync();
        }

        /* Move robot backward */
        private static async void MoveBackward(Ev3Command command)
        {
            //Show message
            Console.WriteLine("Moving Backward...");

            //Set distance in cm
            uint distance = 30; //Default to 30 cm
            if (!string.IsNullOrWhiteSpace(command.Value))
            {
                //If custom distance was given, set and display message
                uint.TryParse(command.Value, out distance);
            }

            //Multiply distance to get steps required
            distance *= 100;
            //Command robot
            _brick.BatchCommand.Initialize(CommandType.DirectNoReply);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.B, Polarity.Backward);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.C, Polarity.Backward);
            _brick.BatchCommand.StepMotorAtSpeed(OutputPort.B, 100, distance, false);
            _brick.BatchCommand.StepMotorAtSpeed(OutputPort.C, 100, distance, false);
            await _brick.BatchCommand.SendCommandAsync();
        }

        /* Turn robot left */
        private static async void MoveLeft(Ev3Command command)
        {
            //Show message
            Console.WriteLine("\n\nMoving Left...\n\n");

            //Set rotation in degrees
            uint degrees = 90;  //Default to 90 degrees
            if (!string.IsNullOrWhiteSpace(command.Value))
            {
                //If custom rotation was given, set and output
                uint.TryParse(command.Value, out degrees);
                Console.WriteLine("\nValue: {0}...\n\n", degrees);
            }

            //Command robot
            _brick.BatchCommand.Initialize(CommandType.DirectNoReply);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.B, Polarity.Backward);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.C, Polarity.Forward);
            //Steps calculated using factor set in config file
            _brick.BatchCommand.StepMotorAtPower(OutputPort.B, 100, Convert.ToUInt32(_StepsPerDegreeTurn * degrees), false);
            //Steps calculated using factor set in config file
            _brick.BatchCommand.StepMotorAtPower(OutputPort.C, 100, Convert.ToUInt32(_StepsPerDegreeTurn * degrees), false);
            await _brick.BatchCommand.SendCommandAsync();
        }

        /* Turn robot right */
        private static async void MoveRight(Ev3Command command)
        {
            //Show message
            Console.WriteLine("\n\nMoving Right...\n\n");

            //Set rotation in degrees
            uint degrees = 90;  //Default to 90 degrees
            if (!string.IsNullOrWhiteSpace(command.Value))
            {
                //If custom rotation was given, set and output
                uint.TryParse(command.Value, out degrees);
                Console.WriteLine("\nValue: {0}...\n\n", degrees);
            }

            //Command robot
            _brick.BatchCommand.Initialize(CommandType.DirectNoReply);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.B, Polarity.Forward);
            _brick.BatchCommand.SetMotorPolarity(OutputPort.C, Polarity.Backward);
            //Steps calculated using factor set in config file
            _brick.BatchCommand.StepMotorAtPower(OutputPort.B, 100, Convert.ToUInt32(_StepsPerDegreeTurn * degrees), false);
            //Steps calculated using factor set in config file
            _brick.BatchCommand.StepMotorAtPower(OutputPort.C, 100, Convert.ToUInt32(_StepsPerDegreeTurn * degrees), false);
            await _brick.BatchCommand.SendCommandAsync();
        }
    }
}

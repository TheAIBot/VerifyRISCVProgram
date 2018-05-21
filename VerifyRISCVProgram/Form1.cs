using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using CefSharp;
using CefSharp.WinForms;
using System.Text.RegularExpressions;

namespace VerifyRISCVProgram
{
    public partial class Form1 : Form
    {
        public ChromiumWebBrowser chromeBrowser;
        private IFrame frame = null;

        public Form1()
        {
            InitializeComponent();
            CefSettings settings = new CefSettings();
            Cef.Initialize(settings);
            chromeBrowser = new ChromiumWebBrowser("file:///venus/index.html");
            chromeBrowser.Location = new System.Drawing.Point(12, 12);
            chromeBrowser.MinimumSize = new System.Drawing.Size(20, 20);
            chromeBrowser.Name = "webBrowser1";
            chromeBrowser.Size = new System.Drawing.Size(1518, 564);
            chromeBrowser.TabIndex = 0;

            this.Controls.Add(chromeBrowser);
        }

        private int[] BytesToInts(byte[] bytes)
        {
            int[] ints = new int[bytes.Length / 4];
            for (int i = 0; i < ints.Length; i++)
            {
                ints[i] = BitConverter.ToInt32(bytes, i * 4);
            }

            return ints;
        }

        private string RunScript(string script)
        {
            script =
            "(function() " +
            "{ " +
                script +
            "})();";
            string result =  (string)frame.EvaluateScriptAsync(script, null)?.Result?.Result;
            if (result == null)
            {
                this.Invoke(new Action(() => textBox1.AppendText("Failed to run the script: " + script + Environment.NewLine)));
                throw new Exception("");
            }
            return result;
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            chromeBrowser.FrameLoadEnd += async (s, args) =>
            {
                if (args.Frame.IsMain)
                {
                    frame = await Task.Factory.StartNew(() => chromeBrowser.GetMainFrame());
                    //await Task.Delay(400);

                    try
                    {
                        //get all unique test names
                        string[] files = Directory.GetFiles("instructionTests");
                        HashSet<string> distinctFiles = new HashSet<string>();
                        foreach (var file in files)
                        {
                            distinctFiles.Add(file.Split('.')[0]);
                        }

                        //go to the simulator
                        //it's possible to input code from the simulator
                        RunScript("driver.openSimulator(); return \"\";");
                        foreach (string file in distinctFiles)
                        {
                            string nonFormattedAssembly = File.ReadAllText(file + ".s");
                            byte[] instructionsInBytes = File.ReadAllBytes(file + ".bin");
                            byte[] registersInBytes = File.ReadAllBytes(file + ".res");

                            string assembly = Regex.Replace(nonFormattedAssembly, "\n", "\\n");
                            int[] instructions = BytesToInts(instructionsInBytes);
                            int[] registers = BytesToInts(registersInBytes);
                            try
                            {
                                ////input assembly code into venus
                                RunScript("document.getElementById(\"asm-editor\").value = \"" + assembly + "\"; driver.openSimulator(); return \"\";");
                                //move to simulator and run code
                                RunSimulator(instructions);

                                //collect information from venus
                                int[] venusInstructions = GetVenusInstructions(instructions.Length);
                                int[] venusRegisterValues = GetVenusRegisterValues();

                                ////check if everything is correct
                                CheckIfCorrect(file, instructions, registers, venusInstructions, venusRegisterValues);
                            }
                            catch (Exception)
                            {

                            }
                        }
                    }
                    catch (Exception eee)
                    {
                        MessageBox.Show(eee.Message + Environment.NewLine + eee.StackTrace);
                    }
                }
            };
        }

        private void RunSimulator(int[] instructions)
        {
            //to see when the program has run, a difference in the registers
            //has to be observed. To do this, the registers will be set to qqq
            //so when the program runs, it will set the registers to their correct
            //values which can be observed as not all registers being qqq anymore.
            string script =
            "for (var i = 0; i < 32; i++)" +
            "{" +
                "document.getElementById(\"reg-\" + i + \"-val\").value = \"qqq\";" +
            "}" +
            "return \"\";";
            RunScript(script);
            //wait for all registersto change to qqq before continuing
            while (GetVenusRegisterValues().Any(x => x != 0));

            if (instructions.Length > 1000)
            {
                RunScript("driver.run(); return \"\";");
            }
            else
            {
                for (int i = 0; i < instructions.Length; i++)
                {
                    RunScript("driver.step(); return \"\";");
                }
            }

            //wait for some of the registers to change to non qqq
            //which means that the registers has updated
            while (GetVenusRegisterValues().All(x => x == 0));
        }

        private void CheckIfCorrect(string file, int[] instructions, int[] registers, int[] venusInstructions, int[] venusRegisterValues)
        {
            bool isRegistersSame = IsRegistersSame(registers, venusRegisterValues);
            string text;
            if (instructions.Length != venusInstructions.Length)
            {
                text = file + " Instruction length doesn't match" + Environment.NewLine;
            }
            else if (registers.Length != venusRegisterValues.Length)
            {
                text = file + " Registers length doesn't match" + Environment.NewLine;
            }
            else if (!instructions.SequenceEqual(venusInstructions))
            {
                text = file + " Instructions doesn't match" + Environment.NewLine;
            }
            else if (!isRegistersSame)
            {
                text = file + " Registers doesn't match" + Environment.NewLine;
            }
            else
            {
                text = file + " Is correct" + Environment.NewLine;
            }

            this.Invoke(new Action(() => textBox1.AppendText(text)));
        }

        private static bool IsRegistersSame(int[] registers, int[] venusRegisterValues)
        {
            bool isRegistersSame = true;
            for (int i = 0; i < 32; i++)
            {
                if (registers[i] != venusRegisterValues[i] && !(i == 2 || i == 3 || i == 10))
                {
                    isRegistersSame = false;
                    break;
                }
            }

            return isRegistersSame;
        }

        private int[] GetVenusRegisterValues()
        {
            
            string script =  
            "var regValues = \"\";" +
            "for (var i = 0; i < 32; i++)" +
            "{" +
                "regValues = regValues + \",\" + document.getElementById(\"reg-\" + i + \"-val\").value;" +
            "}" +
            "return regValues";
            string registerValues = RunScript(script);

            string[] registers = registerValues.Split(',');
            int[] venusRegisterValues = new int[32];
            for (int i = 0; i < 32; i++)
            {
                if (registers[i + 1] == "qqq")
                {
                    venusRegisterValues[i] = 0;
                }
                else
                {
                    venusRegisterValues[i] = Convert.ToInt32(registers[i + 1], 16);
                }
            }

            return venusRegisterValues;
        }

        private int[] GetVenusInstructions(int instructionsCount)
        {
            string script =
            "var instructions = \"\";" +
            "for (var i = 0; i < " + instructionsCount.ToString() + "; i++)" +
            "{" +
                "instructions = instructions + \",\" + document.getElementById(\"instruction-\" + i).childNodes[0].innerText;" +
            "}" +
            "return instructions;";
            string instructionsInString = RunScript(script);

            string[] instructions = instructionsInString.Split(',');
            int[] venusInstructions = new int[instructionsCount];
            for (int i = 0; i < venusInstructions.Length; i++)
            {
                venusInstructions[i] = Convert.ToInt32(instructions[i + 1], 16);
            }

            return venusInstructions;
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            Cef.Shutdown();
        }
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Quickshell.App;

/// <summary>
/// One of a server's sign-in questions, put to the person at the window (QS218).
///
/// <para><b>The server's own words, as written.</b> It is the server that knows whether it wants a
/// password, a one-time code or a push approved, so the prompt is shown as it came and this client
/// adds only who is asking: the account and the host.</para>
///
/// <para><b>Hidden where the server says hidden.</b> A prompt the server marks as not echoed is a
/// <see cref="PasswordBox"/>, and only a hidden answer to a password prompt offers to be remembered —
/// a one-time code kept would be wrong the next time it was offered.</para>
/// </summary>
public sealed class SignInDialog : Window
{
    private readonly PasswordBox _hidden = new() { MinWidth = 320, Padding = new Thickness(4) };
    private readonly TextBox _shown = new() { MinWidth = 320, Padding = new Thickness(4) };
    private readonly CheckBox _remember = new() { Content = "Remember this password", Margin = new Thickness(0, 10, 0, 0) };
    private readonly SignInQuestion _question;

    /// <summary>Builds the dialog over one question.</summary>
    public SignInDialog(SignInQuestion question)
    {
        ArgumentNullException.ThrowIfNull(question);

        _question = question;

        Title = "Sign in";
        AutomationProperties.SetAutomationId(this, "SignIn");

        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 560;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        StackPanel body = new() { Margin = new Thickness(18) };

        body.Children.Add(new TextBlock
        {
            Text = $"{question.Endpoint.User}@{question.Endpoint.Host} asks:",
            Margin = new Thickness(0, 0, 0, 6),
            Opacity = 0.7,
        });

        TextBlock prompt = new()
        {
            Text = question.Prompt.Trim(),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        body.Children.Add(prompt);

        Control field = question.Echoed ? _shown : _hidden;

        // Named for the server's question, so a screen reader reads what is being asked.
        AutomationProperties.SetName(field, question.Prompt.Trim());
        AutomationProperties.SetAutomationId(field, "Answer");
        body.Children.Add(field);

        if (question.MayRemember)
        {
            AutomationProperties.SetAutomationId(_remember, "Remember");
            body.Children.Add(_remember);

            // What remembering does and does not protect, in the store's own words, so every
            // surface that offers it says the same thing.
            body.Children.Add(new TextBlock
            {
                Text = Quickshell.Transport.SecretStore.WhatThisDoesNotProtect,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(20, 4, 0, 0),
                Opacity = 0.7,
                FontSize = 11,
            });
        }

        StackPanel row = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        Button signIn = new() { Content = "Sign in", Padding = new Thickness(14, 4, 14, 4), IsDefault = true };
        Button decline = new()
        {
            Content = "Don't sign in",
            Padding = new Thickness(14, 4, 14, 4),
            Margin = new Thickness(8, 0, 0, 0),
            IsCancel = true,
        };

        AutomationProperties.SetAutomationId(signIn, "sign-in");
        AutomationProperties.SetAutomationId(decline, "decline");

        signIn.Click += (_, _) =>
        {
            Answered = new SignInAnswer(question.Echoed ? _shown.Text : _hidden.Password,
                                        question.MayRemember && _remember.IsChecked == true);
            Close();
        };

        decline.Click += (_, _) => Close();

        row.Children.Add(signIn);
        row.Children.Add(decline);
        body.Children.Add(row);

        Content = body;

        Loaded += (_, _) => field.Focus();
    }

    /// <summary>What was answered, or null where the person declined or closed the window.</summary>
    public SignInAnswer? Answered { get; private set; }

    /// <summary>Whether keeping the answer is offered, which a test reads.</summary>
    public bool OffersToRemember => _question.MayRemember;
}

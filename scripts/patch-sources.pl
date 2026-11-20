#!/usr/bin/perl
# Rewrites the C# that AssetRipper decompiles from the 2012 game so it compiles
# and behaves correctly on Unity 2022.3. Runs against the user's own rip; no game
# code ships with this repository.
#
# usage: patch-sources.pl <Assets dir>
use strict;
use warnings;

my $assets = shift or die "usage: $0 <Assets dir>\n";
my $scripts = "$assets/Scripts/Assembly-CSharp";
my $plugins = "$assets/Plugins";

sub slurp { local $/; open my $fh, '<', $_[0] or die "$_[0]: $!\n"; my $s = <$fh>; close $fh; $s }
sub spew  { open my $fh, '>', $_[0] or die "$_[0]: $!\n"; print $fh $_[1]; close $fh }

# Removed component shortcuts (Unity 2018.4 / 2019.x).
my %shortcut = (renderer => 'Renderer', camera => 'Camera', rigidbody => 'Rigidbody');

sub generic {
    my ($src) = @_;
    for my $prop (keys %shortcut) {
        my $type = $shortcut{$prop};
        $src =~ s/\bbase\.$prop\b/GetComponent<$type>()/g;
        $src =~ s/\.$prop\b(?!\s*\()/.GetComponent<$type>()/g;
    }

    # Screen.lockCursor was removed in Unity 5.
    $src =~ s/^([ \t]*)Screen\.lockCursor = true;/$1Cursor.lockState = CursorLockMode.Locked;\n$1Cursor.visible = false;/mg;
    $src =~ s/^([ \t]*)Screen\.lockCursor = false;/$1Cursor.lockState = CursorLockMode.None;\n$1Cursor.visible = true;/mg;

    # Application.LoadLevel is deprecated in favour of SceneManager.
    if ($src =~ s/\bApplication\.LoadLevel\(/SceneManager.LoadScene(/g) {
        $src =~ s/^using UnityEngine;$/using UnityEngine;\nusing UnityEngine.SceneManagement;/m
            or die "no 'using UnityEngine;' to extend\n";
    }

    $src =~ s/\bStartCoroutine_Auto\(/StartCoroutine(/g;
    return $src;
}

# Applies one targeted rewrite and insists it matched exactly $want times, so a
# different game build fails loudly instead of producing a subtly wrong port.
sub rewrite {
    my ($file, $srcref, $want, $re, $repl) = @_;
    my $n = () = $$srcref =~ /$re/g;
    die "$file: expected $want match(es) of $re, found $n\n" unless $n == $want;
    $$srcref =~ s/$re/$repl->()/ge;
}

my %specific = (
    # UnityScript compiled this coroutine to a Boo.Lang generator class. Boo.Lang
    # is gone, so the state machine is replaced with an equivalent C# iterator.
    "$plugins/Assembly-UnityScript-firstpass/CharacterMotor.cs" => sub {
        my ($f, $s) = @_;
        rewrite($f, $s, 1, qr/^using Boo\.Lang;\n/m, sub { '' });
        rewrite($f, $s, 1, qr/^\t\[Serializable\]\n\tinternal sealed class _0024SubtractNewPlatformVelocity\w*\b.*?^\t\}\n\n/ms, sub { '' });
        rewrite($f, $s, 1, qr/^\t\treturn new _0024SubtractNewPlatformVelocity\w*\(this\)\.GetEnumerator\(\);\n/m, sub { <<'CS' });
		if (!movingPlatform.enabled
			|| (movingPlatform.movementTransfer != MovementTransferOnJump.InitTransfer
				&& movingPlatform.movementTransfer != MovementTransferOnJump.PermaTransfer))
		{
			yield break;
		}
		if (movingPlatform.newPlatform)
		{
			// A new platform needs two FixedUpdates before its velocity is known.
			Transform platform = movingPlatform.activePlatform;
			yield return new WaitForFixedUpdate();
			yield return new WaitForFixedUpdate();
			if (grounded && platform == movingPlatform.activePlatform)
			{
				yield return null;
			}
		}
		movement.velocity -= movingPlatform.platformVelocity;
CS
    },

    # Unity 5 dropped the legacy 2x light multiplier. The importer doubles every
    # serialized light, but intensities set from script must be doubled by hand or
    # the flashlight runs at half brightness.
    "$scripts/PlayerScript.cs" => sub {
        my ($f, $s) = @_;
        rewrite($f, $s, 1, qr/torch\.intensity = battery - 0\.015f;/, sub { 'torch.intensity = (battery - 0.015f) * 2f;' });
        rewrite($f, $s, 1, qr/torch\.intensity = battery;/, sub { 'torch.intensity = battery * 2f;' });
    },
    "$scripts/LoseScript.cs" => sub {
        my ($f, $s) = @_;
        rewrite($f, $s, 1, qr/(\s)l1\.intensity = 1f;/, sub { "$1l1.intensity = 2f;" });
        rewrite($f, $s, 1, qr/(\s)l2\.intensity = 1f;/, sub { "$1l2.intensity = 2f;" });
    },

    # SlenderMan validates teleport targets with a probe whose OnCollisionStay
    # rejects spots inside geometry. PhysX 3 stops that callback once the body
    # sleeps, so the probe would accept spots inside trees. Keep it awake.
    "$scripts/TestScript.cs" => sub {
        my ($f, $s) = @_;
        rewrite($f, $s, 1, qr/^(\tpublic Transform rhand;\n)/m, sub { $1 . <<'CS' });

	private void Awake()
	{
		Rigidbody body = GetComponent<Rigidbody>();
		if (body != null)
		{
			body.sleepThreshold = 0f;
		}
	}
CS
    },
);

my @files = (glob("$scripts/*.cs"), glob("$plugins/*/*.cs"));
die "no scripts under $assets\n" unless @files;
for my $file (@files) {
    my $src = generic(slurp($file));
    $specific{$file}->($file, \$src) if $specific{$file};
    spew($file, $src);
}
for my $file (keys %specific) {
    die "$file: missing from the rip\n" unless -f $file;
}
print "patched ", scalar(@files), " scripts\n";

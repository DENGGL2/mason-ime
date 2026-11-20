import unittest
import sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import correction_settings as settings


class CorrectionSettingsTests(unittest.TestCase):
    def test_both_modes_round_trip_and_preserve_user_patches(self):
        original = 'patch:\n  switches/@1/reset: 0\n  speller/algebra/+:\n    - fuzz/en/eng/\n  engine/filters/@next: lua_filter@*mason_candidates\n'
        for enabled in (True,False):
            for flexible in (True,False):
                config=dict(correction_enabled=enabled,learning_flexible=flexible)
                merged=settings.build_yaml(original,config)
                self.assertTrue(merged.startswith(original))
                self.assertEqual(settings.load(merged),config)
                self.assertEqual(settings.build_yaml(merged,config),merged)

    def test_legacy_config_migrates_without_changing_correction_choice(self):
        original='patch:\n# mason-correction-settings-begin\n  mason_correction/enabled: false\n# mason-correction-settings-end\n'
        values=settings.load(original)
        self.assertEqual(values,dict(correction_enabled=False,learning_flexible=False))
        merged=settings.build_yaml(original,values)
        self.assertEqual(merged.count(settings.BEGIN),1)
        self.assertIn('mason_correction/enabled: false',merged)
        self.assertIn('mason_learning/flexible: false',merged)

    def test_rejects_ambiguous_config_and_wrong_boolean(self):
        for key in ('mason_correction/enabled','mason_learning/flexible'):
            with self.assertRaises(ValueError):
                settings.build_yaml('patch:\n  '+key+': true\n  '+key+': false\n',{})
        with self.assertRaises(ValueError):
            settings.build_yaml('patch: {foo: 1}\n',{})
        with self.assertRaises(ValueError):
            settings.validate(dict(learning_flexible=1))


if __name__=='__main__':
    unittest.main()

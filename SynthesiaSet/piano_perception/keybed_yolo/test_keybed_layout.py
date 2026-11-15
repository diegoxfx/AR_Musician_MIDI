"""Regression checks for sparse rectified key guides and abstention."""

from dataclasses import replace
import unittest

import cv2
import numpy as np

from evaluate import Frame, Instance, Mask
from keybed_geometry import KeybedFit, UNIT_QUAD, select_keybed
from keybed_layout import CT_S200_PROFILE, KeyboardProfile, LayoutConfig, reconstruct_layout


FRAME = Frame(440, 920, 23, 41, 850, 300, 1700, 700)
QUAD = ((110, 43), (735, 68), (798, 239), (47, 206))
WHITE_COUNT = 36
WHITE_ORDINALS = (1, 7, 12, 17, 23, 29, 34)
PROJECTOR = cv2.getPerspectiveTransform(UNIT_QUAD, np.float32(QUAD))
RECTIFIER = cv2.getPerspectiveTransform(np.float32(QUAD), UNIT_QUAD)


def project(points, transform):
    return cv2.perspectiveTransform(np.float32(points).reshape(-1, 1, 2), transform).reshape(-1, 2)


def detection(vertices, class_id, score, *, content_local=False):
    points = np.asarray(vertices) if content_local else project(vertices, PROJECTOR)
    points = points + (FRAME.left, FRAME.top)
    image = np.zeros((FRAME.height, FRAME.width), dtype=np.uint8)
    cv2.fillPoly(image, [np.rint(points * 256).astype(np.int32)], 1, shift=8)
    ys, xs = np.nonzero(image)
    x, y, x2, y2 = xs.min(), ys.min(), xs.max() + 1, ys.max() + 1
    return Instance(class_id, np.float32([x, y, x2, y2]), Mask.create(x, y, image[y:y2, x:x2]), score)


def bed(quad=QUAD, score=.95):
    return detection(quad, 0, score, content_local=True)


def white(ordinal, score=.82, *, end=.98):
    center = (ordinal + .5) / WHITE_COUNT
    left, right = center - .45 / WHITE_COUNT, center + .45 / WHITE_COUNT
    notch = center + .30 / WHITE_COUNT
    return detection(((notch, .03), (right, .03), (right, end), (left, end),
                      (left, .66), (notch, .66)), 1, score)


def black(boundary, score=.86):
    left, right = (boundary - .24) / WHITE_COUNT, (boundary + .24) / WHITE_COUNT
    return detection(((left, .03), (right, .03), (right, .58), (left, .58)), 2, score)


def observations(ordinals=WHITE_ORDINALS):
    return [bed(), *(white(ordinal) for ordinal in ordinals)]


def preview(predictions, *, quad=QUAD, profile=CT_S200_PROFILE, bed_index=0, config=LayoutConfig()):
    # Deliberately provide no trusted support metadata. The raw mask must
    # provide support, including when the caller has supplied the quad.
    fit = KeybedFit(quad, bed_index, .95, 1, "external", 1, 1, 0, ())
    return reconstruct_layout(predictions, FRAME, fit, profile, config)


class SparseLayoutTests(unittest.TestCase):
    def assert_rejected(self, result, reason):
        self.assertEqual(result.status, "rejected")
        self.assertRegex(result.reason, reason)
        self.assertEqual(result.guides, ())
        self.assertIsNone(result.evidence)

    def test_skipped_white_slots_and_asymmetric_letterbox(self):
        predictions = [white(17), bed(), *(white(i) for i in WHITE_ORDINALS if i != 17), black(9.13)]
        fit = select_keybed(predictions[:2], FRAME)
        result = reconstruct_layout(predictions, FRAME, fit, CT_S200_PROFILE)
        self.assertEqual(result.status, "needs_confirmation", result.reason)
        self.assertEqual(result.evidence.outline_support, "raw_bed_edges")
        self.assertEqual([guide.midi_note for guide in result.guides], list(range(36, 97)))
        self.assertEqual(result.evidence.bed_detection_index, 1)
        self.assertGreater(result.evidence.bed_iou, .96)
        self.assertAlmostEqual(result.evidence.white_spacing, 1 / WHITE_COUNT, delta=.00025)
        self.assertGreater(result.evidence.white_anchor_span, .89)
        self.assertLess(result.evidence.white_residual_rms, .06)

        whites = [guide for guide in result.guides if not guide.is_black]
        self.assertEqual([guide.white_ordinal for guide in whites if guide.observed], list(WHITE_ORDINALS))
        self.assertEqual(whites[17].detection_index, 0)
        self.assertEqual(whites[17].detection_score, .82)
        self.assertIsNone(whites[0].detection_index)
        self.assertIsNone(whites[0].detection_score)
        for ordinal, guide in enumerate(whites):
            rectified = project(guide.segment, RECTIFIER)
            np.testing.assert_allclose(rectified[:, 0], (ordinal + .5) / WHITE_COUNT, atol=.002)
            np.testing.assert_allclose(rectified[:, 1], [0, 1], atol=.012)
        blacks = [guide for guide in result.guides if guide.is_black]
        observed_black, = [guide for guide in blacks if guide.observed]
        self.assertEqual(observed_black.detection_index, len(predictions) - 1)
        self.assertAlmostEqual(observed_black.normalized_x, 9.13 / WHITE_COUNT, delta=.002)
        np.testing.assert_allclose(project(observed_black.segment, RECTIFIER)[:, 1], [0, .6], atol=.012)

    def test_duplicates_and_outliers_do_not_claim_additional_slots(self):
        predictions = [*observations(), white(7, .98), white(16.50, .99), white(26, end=.61)]
        result = preview(predictions)
        self.assertEqual(result.status, "needs_confirmation", result.reason)
        self.assertEqual(result.evidence.white_anchor_indices, (1, 8, 3, 4, 5, 6, 7))
        self.assertEqual(sum(guide.observed for guide in result.guides), len(WHITE_ORDINALS))

    def test_thinned_reconstruction_can_replay_independently_supported_white_edges(self):
        partial_bed = ((.055, 0), (1, 0), (1, 1), (.055, 1))
        predictions = [detection(partial_bed, 0, .18), *(white(i) for i in range(WHITE_COUNT))]
        fit = select_keybed(predictions, FRAME)
        self.assertEqual(fit.method, "white-key-edges")
        anchors = [i + 1 for i in WHITE_ORDINALS]
        result = reconstruct_layout(predictions, FRAME, fit, CT_S200_PROFILE, anchor_indices=anchors)
        self.assertEqual(result.status, "needs_confirmation", result.reason)
        self.assertEqual(result.evidence.outline_support, "replayed_white_edges")
        self.assertEqual(result.evidence.white_anchor_indices, tuple(anchors))

        changed_corner = (fit.quad[0][0] + 1, fit.quad[0][1])
        changed_fit = replace(fit, quad=(changed_corner, *fit.quad[1:]))
        self.assert_rejected(reconstruct_layout(predictions, FRAME, changed_fit, CT_S200_PROFILE), "verified white-key")
        self.assert_rejected(reconstruct_layout(predictions, FRAME, fit, CT_S200_PROFILE, anchor_indices=[]),
                             "too few reliable white-key anchors")
        with self.assertRaisesRegex(ValueError, "indices must refer to raw detections"):
            reconstruct_layout(predictions, FRAME, fit, CT_S200_PROFILE, anchor_indices=[False])

    def test_none_and_count_only_abstain_while_pattern_has_no_octave(self):
        for profile in (None, KeyboardProfile(61)):
            with self.subTest(profile=profile):
                self.assert_rejected(preview(observations(), profile=profile), "explicit keyboard count and start pitch class")
        result = preview(observations(), profile=KeyboardProfile(61, start_pitch_class=0))
        self.assertEqual(result.status, "needs_confirmation", result.reason)
        self.assertEqual([guide.pitch_class for guide in result.guides], [i % 12 for i in range(61)])
        self.assertTrue(all(guide.midi_note is None for guide in result.guides))

    def test_wrong_profile_count_is_rejected_by_spacing(self):
        result = preview(observations(range(WHITE_COUNT)), profile=KeyboardProfile(88, first_midi=21))
        self.assert_rejected(result, "spacing")

    def test_same_white_count_with_conflicting_black_pattern(self):
        predictions = [*observations(), *(black(i) for i in (2, 6, 9, 13, 16, 20, 23, 27, 30, 34))]
        self.assertEqual(preview(predictions).status, "needs_confirmation")
        result = preview(predictions, profile=KeyboardProfile(61, start_pitch_class=2))
        self.assert_rejected(result, "black-key anchors contradict")

    def test_rejects_all_estimated_or_localized_evidence(self):
        for ordinals in ((), (1, 17, 23, 34)):
            with self.subTest(ordinals=ordinals):
                result = preview([*observations(ordinals), black(9), black(20), black(27)])
                self.assert_rejected(result, "too few reliable white-key anchors")
        for ordinals in ((2, 3, 4, 5, 6, 7), (1, 2, 3, 32, 33, 34), (1, 2, 11, 32, 33, 34)):
            with self.subTest(ordinals=ordinals):
                self.assert_rejected(preview(observations(ordinals)), "too localized")

    def test_missing_clipped_or_unsupported_outline_abstains(self):
        predictions = observations()
        self.assert_rejected(reconstruct_layout(predictions, FRAME, None, CT_S200_PROFILE), "full keybed outline")
        self.assert_rejected(preview(predictions, bed_index=1), "playable-keybed detection")
        self.assert_rejected(preview([bed(score=.18), *predictions[1:]]), "confident playable-keybed")
        self.assert_rejected(preview([bed(((0, 43), *QUAD[1:])), *predictions[1:]]), "clipped image edge")
        partial_quad = ((145, 44), QUAD[1], QUAD[2], (91, 208))
        self.assert_rejected(preview([bed(partial_quad), *predictions[1:]]), "support along all four")
        self.assert_rejected(preview(predictions, quad=partial_quad), "support along all four")

    def test_nonfinite_or_invalid_inputs(self):
        predictions = observations()
        self.assert_rejected(preview(predictions, quad=((float("nan"), 43), *QUAD[1:])), "invalid or clipped corners")
        malformed = replace(predictions[0], mask=replace(predictions[0].mask, x=0))
        self.assert_rejected(preview([malformed, *predictions[1:]]), "mask is outside")
        invalid_frame = replace(FRAME, content_width=FRAME.width)
        fit = select_keybed(predictions[:1], FRAME)
        self.assert_rejected(reconstruct_layout(predictions, invalid_frame, fit, CT_S200_PROFILE), "invalid frame")
        for kwargs in ({"key_count": True}, {"key_count": 0}, {"key_count": 61, "first_midi": 72},
                       {"key_count": 61, "start_pitch_class": 12}, {"key_count": 61, "first_midi": 37},
                       {"key_count": 62, "first_midi": 36},
                       {"key_count": 61, "first_midi": 36, "start_pitch_class": 2}):
            with self.subTest(kwargs=kwargs), self.assertRaises(ValueError):
                KeyboardProfile(**kwargs)
        for invalid in (float("nan"), float("inf"), -1, 2, True):
            with self.subTest(invalid=invalid), self.assertRaises(ValueError):
                LayoutConfig(white_confidence=invalid)
        self.assert_rejected(preview(predictions, config=LayoutConfig(white_confidence=.9)), "too few reliable")


if __name__ == "__main__":
    unittest.main()
